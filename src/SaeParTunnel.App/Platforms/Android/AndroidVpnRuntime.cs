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
    private static string _connectedProfileId = string.Empty;
    private static string _lastError = string.Empty;
    private static string _statusMessage = "VPN Android آماده است.";

    public static event EventHandler<AndroidVpnStatusEventArgs>? StatusChanged;

    public static bool IsConnected => _isConnected;
    public static string ConnectedProfileId => _connectedProfileId;
    public static string LastError => _lastError;
    public static string StatusMessage => _statusMessage;

    public static Task PrepareStartWaitAsync(CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            _lastError = string.Empty;
            _startWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => _startWaiter?.TrySetCanceled(cancellationToken));
            return _startWaiter.Task;
        }
    }

    public static Task PrepareStopWaitAsync(CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            _stopWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => _stopWaiter?.TrySetCanceled(cancellationToken));
            return _stopWaiter.Task;
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
            _stopWaiter?.TrySetResult(true);
            _stopWaiter = null;
            handler = StatusChanged;
            args = new AndroidVpnStatusEventArgs("disconnected", _statusMessage, false);
        }
        try { handler?.Invoke(null, args); } catch { }
    }

    public static void SignalError(string message)
    {
        EventHandler<AndroidVpnStatusEventArgs>? handler;
        AndroidVpnStatusEventArgs args;
        lock (Gate)
        {
            _isConnected = false;
            _connectedProfileId = string.Empty;
            _lastError = message ?? "Android VPN failed.";
            _statusMessage = "خطای VPN Android: " + _lastError;
            _startWaiter?.TrySetException(new InvalidOperationException(_lastError));
            _startWaiter = null;
            _stopWaiter?.TrySetResult(true);
            _stopWaiter = null;
            handler = StatusChanged;
            args = new AndroidVpnStatusEventArgs("error", _statusMessage, false);
        }
        try { handler?.Invoke(null, args); } catch { }
    }
}
#endif
