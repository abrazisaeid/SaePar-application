#if ANDROID
namespace SaeParTunnel.App.Platforms.Android;

internal sealed record AndroidVpnStatusEventArgs(
    string Stage,
    string Message,
    bool? Connected = null,
    string? ProfileId = null);

internal static class AndroidVpnRuntime
{
    private static readonly object Gate = new();
    private static TaskCompletionSource<bool>? _startWaiter;
    private static TaskCompletionSource<bool>? _stopWaiter;
    private static volatile bool _isConnected;
    private static volatile bool _isServiceRunning;
    private static bool _stopRequested;
    private static bool _startCancelledByUser;
    private static string _connectedProfileId = string.Empty;
    private static string _lastError = string.Empty;
    private static string _statusMessage = "VPN Android آماده است.";

    public static event EventHandler<AndroidVpnStatusEventArgs>? StatusChanged;

    public static bool IsConnected => _isConnected;
    public static bool IsServiceRunning => _isServiceRunning;
    public static string ConnectedProfileId => _connectedProfileId;
    public static string LastError => _lastError;
    public static string StatusMessage => _statusMessage;
    public static bool WasStartCancelledByUser { get { lock (Gate) return _startCancelledByUser; } }

    public static Task PrepareStartWaitAsync(CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            _lastError = string.Empty;
            _stopRequested = false;
            _startCancelledByUser = false;
            _startWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = _startWaiter;
            var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            _ = waiter.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            return waiter.Task;
        }
    }

    public static Task PrepareStopWaitAsync(CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            // The app and notification can request shutdown concurrently. Each
            // caller has its own cancellation; neither may replace the shared
            // service-destruction acknowledgement or cancel another caller.
            _stopWaiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _stopWaiter.Task.WaitAsync(cancellationToken);
        }
    }

    public static void ReportStatus(string stage, string message, bool? connected = null, string? profileId = null)
    {
        EventHandler<AndroidVpnStatusEventArgs>? handler;
        AndroidVpnStatusEventArgs args;
        lock (Gate)
        {
            _statusMessage = message ?? string.Empty;
            if (connected.HasValue)
                _isConnected = connected.Value;
            if (profileId is not null)
                _connectedProfileId = profileId;
            handler = StatusChanged;
            args = new AndroidVpnStatusEventArgs(stage, _statusMessage, connected, profileId);
        }
        try { handler?.Invoke(null, args); } catch { }
    }

    public static void SignalConnected(string profileId)
    {
        EventHandler<AndroidVpnStatusEventArgs>? handler;
        AndroidVpnStatusEventArgs args;
        lock (Gate)
        {
            _isConnected = true;
            _connectedProfileId = profileId ?? string.Empty;
            _lastError = string.Empty;
            _statusMessage = "VPN Android با موفقیت برقرار شد.";
            _startWaiter?.TrySetResult(true);
            _startWaiter = null;
            handler = StatusChanged;
            args = new AndroidVpnStatusEventArgs("connected", _statusMessage, true, _connectedProfileId);
        }
        try { handler?.Invoke(null, args); } catch { }
    }

    public static void SignalDisconnected()
    {
        EventHandler<AndroidVpnStatusEventArgs>? handler;
        AndroidVpnStatusEventArgs args;
        lock (Gate)
        {
            _isConnected = false;
            _connectedProfileId = string.Empty;
            _statusMessage = "VPN Android قطع است.";
            _startWaiter?.TrySetCanceled();
            _startWaiter = null;
            _stopWaiter?.TrySetResult(true);
            _stopWaiter = null;
            handler = StatusChanged;
            args = new AndroidVpnStatusEventArgs("disconnected", _statusMessage, false);
        }
        try { handler?.Invoke(null, args); } catch { }
    }

    public static void SignalError(string message, bool startupFailure = true)
    {
        EventHandler<AndroidVpnStatusEventArgs>? handler;
        AndroidVpnStatusEventArgs args;
        lock (Gate)
        {
            _isConnected = false;
            _connectedProfileId = string.Empty;
            _lastError = message ?? "Android VPN failed.";
            _statusMessage = "خطای VPN Android: " + _lastError;
            _startWaiter?.TrySetException(startupFailure
                ? new SaeParTunnel.Core.Models.TunnelStartupException(_lastError)
                : new SaeParTunnel.Core.Models.TunnelValidationException(_lastError));
            _startWaiter = null;
            handler = StatusChanged;
            args = new AndroidVpnStatusEventArgs("error", _statusMessage, false);
            _stopRequested = true;
        }
        try { handler?.Invoke(null, args); } catch { }
    }

    public static void CancelStartWait()
    {
        lock (Gate)
        {
            _startWaiter?.TrySetCanceled();
            _startWaiter = null;
        }
    }

    public static void MarkUserStopRequested()
    {
        lock (Gate) { if (_startWaiter is not null) _startCancelledByUser = true; }
        TryRequestStop();
        ReportStatus("disconnecting", "در حال قطع VPN از اعلان...");
    }

    public static void ReportServiceCreated() => _isServiceRunning = true;

    public static void ReportServiceStopped()
    {
        lock (Gate)
        {
            _isServiceRunning = false;
            _stopRequested = false;
        }
        SignalDisconnected();
    }

    public static bool TryRequestStop()
    {
        lock (Gate)
        {
            if (_stopRequested) return false;
            _stopRequested = true;
            return true;
        }
    }

    public static void ResetStopRequest()
    {
        lock (Gate) { _stopRequested = false; }
    }
}
#endif
