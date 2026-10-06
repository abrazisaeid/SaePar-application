#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Com.Saepar.Tunnel.Bridge;
using Microsoft.Maui.ApplicationModel;
using SaeParTunnel.Core.Services;
using SaeParTunnel.Core.Models;
using System.Text.Json;
using Process = Android.OS.Process;

namespace SaeParTunnel.App.Platforms.Android;

// A private bound service owns Go/JNI for probes. The VPN's native core stays in
// the main process, so cancellation can terminate a stuck probe safely.
internal sealed class AndroidProbeProcess
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProbeConnection? _connection;
    private readonly Context _context = global::Android.App.Application.Context;
    private string CachePath => _context.CacheDir!.AbsolutePath;

    public async Task<string> RunAsync(string config, int port, int seconds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var store = new TunnelConfigurationStore(Path.Combine(CachePath, "probe-config"));
        string? token = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                token = await store.WriteAsync(config, startup.Token).ConfigureAwait(false);
                var connection = await GetReadyConnectionAsync(startup.Token).ConfigureAwait(false);
                // Cold service startup gets its own budget; native work remains
                // bounded once the handshake finishes.
                deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 4));
                Task<string>? result = null;
                await MainThread.InvokeOnMainThreadAsync(() => { result = connection.Send(token, port, seconds); })
                    .ConfigureAwait(false);
                return await result!.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await ResetAsync().ConfigureAwait(false);
                throw new TunnelStartupException("موتور تست پاسخ نداد و متوقف شد؛ تلاش بعدی با موتور تازه انجام می‌شود. سرورهای ذخیره‌شده حفظ شدند.");
            }
            catch (Exception ex) when (ex is System.OperationCanceledException or TunnelStartupException)
            {
                await ResetAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await ResetAsync().ConfigureAwait(false);
                throw new TunnelStartupException("ارتباط با موتور تست قطع شد؛ دوباره امتحان کن.", ex);
            }
        }
        finally
        {
            if (token is not null)
            {
                store.Delete(token);
                try { File.Delete(Path.Combine(CachePath, $"probe-{token}.json")); } catch (IOException) { }
            }
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ResetAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<ProbeConnection> GetReadyConnectionAsync(CancellationToken cancellationToken)
    {
        // Android may briefly deliver the old binder while processing a worker
        // death. Retry only the handshake; never repeat a running native probe.
        for (var attempt = 0; ; attempt++)
        {
            var connection = await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (_connection is not null) return _connection;
                var created = new ProbeConnection();
                using var intent = new Intent(_context, typeof(SaeParProbeService));
                if (!_context.BindService(intent, created, Bind.AutoCreate))
                {
                    created.Dispose();
                    throw new TunnelStartupException("موتور تست اندروید راه‌اندازی نشد.");
                }
                return _connection = created;
            }).ConfigureAwait(false);
            try
            {
                await connection.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (connection.IsClosed) throw new TunnelStartupException("موتور تست قبلی بسته شده است.");
                return connection;
            }
            catch (TunnelStartupException) when (attempt < 2)
            {
                await ResetAsync().ConfigureAwait(false);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ResetAsync()
    {
        var pid = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var connection = _connection;
            _connection = null;
            if (connection is null) return 0;
            try { _context.UnbindService(connection); }
            catch (Java.Lang.IllegalArgumentException) { }
            var workerPid = connection.Pid;
            // PID comes only from the non-exported probe service handshake.
            // Never terminate the process hosting the app or the real VPN.
            if (workerPid > 0 && workerPid != Process.MyPid()) Process.KillProcess(workerPid);
            connection.Close();
            connection.Dispose();
            return workerPid;
        }).ConfigureAwait(false);
        if (pid <= 0 || pid == Process.MyPid()) return;
        for (var attempt = 0; attempt < 80 && Directory.Exists($"/proc/{pid}"); attempt++)
            await Task.Delay(25).ConfigureAwait(false);
    }

    private sealed class ProbeConnection : Java.Lang.Object, IServiceConnection
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<string>? _result;
        private Messenger? _remote;
        private readonly ReplyHandler _handler;
        private readonly Messenger _reply;
        private int _closed;
        public bool IsClosed => Volatile.Read(ref _closed) != 0;
        public int Pid { get; private set; }
        public ProbeConnection() { _handler = new ReplyHandler(this); _reply = new Messenger(_handler); }

        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service is null) { Close(); return; }
            _remote = new Messenger(service);
            using var hello = Message.Obtain(null, SaeParProbeService.Hello);
            hello.ReplyTo = _reply;
            try { _remote.Send(hello); }
            catch (RemoteException) { Close(); }
        }
        public void OnServiceDisconnected(ComponentName? name) => Close();
        public void OnBindingDied(ComponentName? name) => Close();
        public void OnNullBinding(ComponentName? name) => Close();

        public Task<string> Send(string token, int port, int seconds)
        {
            _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var message = Message.Obtain(null, SaeParProbeService.Run);
            message.ReplyTo = _reply;
            message.Data = new Bundle();
            message.Data.PutString("token", token);
            message.Data.PutInt("port", port);
            message.Data.PutInt("seconds", seconds);
            (_remote ?? throw new TunnelStartupException("ارتباط با موتور تست قطع شد.")).Send(message);
            return _result.Task;
        }

        public void Close()
        {
            Interlocked.Exchange(ref _closed, 1);
            var error = new TunnelStartupException("موتور تست متوقف شد؛ می‌توانی دوباره امتحان کنی.");
            Ready.TrySetException(error);
            _result?.TrySetException(error);
            // Observe errors even if shutdown precedes the caller's await.
            _ = Ready.Task.Exception;
            if (_result?.Task.IsFaulted == true) _ = _result.Task.Exception;
        }

        private sealed class ReplyHandler(ProbeConnection owner) : Handler(Looper.MainLooper!)
        {
            public override void HandleMessage(Message message)
            {
                if (message.What == SaeParProbeService.Hello)
                {
                    owner.Pid = message.Arg1;
                    if (owner.Pid <= 0 || owner.Pid == Process.MyPid()) owner.Close();
                    else owner.Ready.TrySetResult();
                }
                else if (message.What == SaeParProbeService.Result)
                {
                    var error = message.Data?.GetString("error");
                    if (error is not null) owner._result?.TrySetException(new TunnelStartupException(error));
                    else owner._result?.TrySetResult(message.Data?.GetString("response") ?? string.Empty);
                }
            }
        }
    }
}

[Service(Name = "com.saepar.tunnel.SaeParProbeService", Exported = false, Process = ":probe")]
public sealed class SaeParProbeService : Service
{
    internal const int Hello = 1, Run = 2, Result = 3;
    private Messenger? _messenger;
    private ProbeHandler? _handler;
    private int _running;

    public override void OnCreate()
    {
        base.OnCreate();
        _handler = new ProbeHandler(this);
        _messenger = new Messenger(_handler);
    }
    public override IBinder? OnBind(Intent? intent) => _messenger?.Binder;
    public override void OnDestroy()
    {
        base.OnDestroy();
        // The service is never started independently. Last unbind ends its
        // private process, including any JNI call that ignored cancellation.
        Process.KillProcess(Process.MyPid());
    }

    private sealed class ProbeHandler(SaeParProbeService owner) : Handler(Looper.MainLooper!)
    {
        public override void HandleMessage(Message message)
        {
            var reply = message.ReplyTo;
            if (reply is null) return;
            if (message.What == Hello)
            {
                using var ready = Message.Obtain(null, Hello);
                ready.Arg1 = Process.MyPid();
                try { reply.Send(ready); } catch (RemoteException) { }
                return;
            }
            if (message.What != Run) return;
            var token = message.Data?.GetString("token") ?? string.Empty;
            var port = message.Data?.GetInt("port") ?? 0;
            var seconds = Math.Clamp(message.Data?.GetInt("seconds") ?? 4, 1, 7);
            if (Interlocked.CompareExchange(ref owner._running, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                string? path = null;
                using var result = Message.Obtain(null, Result);
                result.Data = new Bundle();
                try
                {
                    if (port is < 1 or > 65535) throw new ArgumentException("Invalid probe port.");
                    var cache = owner.CacheDir!.AbsolutePath;
                    var store = new TunnelConfigurationStore(Path.Combine(cache, "probe-config"));
                    var config = await store.ConsumeAsync(token).ConfigureAwait(false);
#if SAEPAR_PROBE_DIAGNOSTICS
                    if (config == "SAEPAR_TEST_HANG") Thread.Sleep(Timeout.Infinite);
#endif
                    path = Path.Combine(cache, $"probe-{token}.json");
                    await File.WriteAllTextAsync(path, config).ConfigureAwait(false);
                    SaeParXrayBridge.Initialize();
                    var request = JsonSerializer.Serialize(new
                    {
                        apiVersion = 1, method = "ping",
                        payload = new { configPath = path, timeout = seconds, url = "https://cp.cloudflare.com/", proxy = $"socks5://127.0.0.1:{port}" }
                    });
                    result.Data.PutString("response", SaeParXrayBridge.Invoke(request));
                }
                catch (Exception) { result.Data.PutString("error", "موتور تست اندروید با خطا متوقف شد؛ دوباره امتحان کن."); }
                finally
                {
                    if (path is not null) { try { File.Delete(path); } catch (IOException) { } }
                    Interlocked.Exchange(ref owner._running, 0);
                }
                try { reply.Send(result); } catch (RemoteException) { }
            });
        }
    }
}
#endif
