#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Microsoft.Maui;

namespace SaeParTunnel.App;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int VpnPermissionRequestCode = 8241;
    private TaskCompletionSource<bool>? _vpnPermissionTcs;
    private int _vpnPermissionRequestActive;

    public Task<bool> RequestVpnPermissionAsync(Intent permissionIntent, CancellationToken cancellationToken)
    {
        if (_vpnPermissionTcs is not null)
            return _vpnPermissionTcs.Task;

        _vpnPermissionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _vpnPermissionRequestActive, 1);
        cancellationToken.Register(() =>
        {
            Interlocked.Exchange(ref _vpnPermissionRequestActive, 0);
            var waiter = Interlocked.Exchange(ref _vpnPermissionTcs, null);
            waiter?.TrySetCanceled(cancellationToken);
        });

#pragma warning disable CS0618
        StartActivityForResult(permissionIntent, VpnPermissionRequestCode);
#pragma warning restore CS0618
        return _vpnPermissionTcs.Task;
    }

#pragma warning disable CS0672, CS0618
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode == VpnPermissionRequestCode)
        {
            Interlocked.Exchange(ref _vpnPermissionRequestActive, 0);
            var waiter = Interlocked.Exchange(ref _vpnPermissionTcs, null);
            waiter?.TrySetResult(resultCode == Result.Ok || VpnService.Prepare(this) is null);
        }
        base.OnActivityResult(requestCode, resultCode, data);
    }
#pragma warning restore CS0672, CS0618

    protected override void OnResume()
    {
        base.OnResume();
        if (Volatile.Read(ref _vpnPermissionRequestActive) == 0 || _vpnPermissionTcs is null)
            return;

        // Some OEM Android builds do not reliably deliver OnActivityResult for the
        // VPN consent activity. When our activity resumes, verify preparation directly.
        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            if (VpnService.Prepare(this) is not null) return;
            Interlocked.Exchange(ref _vpnPermissionRequestActive, 0);
            var waiter = Interlocked.Exchange(ref _vpnPermissionTcs, null);
            waiter?.TrySetResult(true);
        });
    }
}
#endif
