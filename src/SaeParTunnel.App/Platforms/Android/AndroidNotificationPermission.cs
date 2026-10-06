#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Application = global::Android.App.Application;

namespace SaeParTunnel.App.Platforms.Android;

internal static class AndroidNotificationPermission
{
    private const string RequestedKey = "vpn-notifications-requested";

    public static Task RequestForConnectionAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return;
        if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() == PermissionStatus.Granted) return;
        // A denial must not cause another dialog on every reconnect. The user
        // can enable the notification later from Advanced → VPN notifications.
        if (Preferences.Default.Get(RequestedKey, false)) return;
        Preferences.Default.Set(RequestedKey, true);
        await Permissions.RequestAsync<Permissions.PostNotifications>();
    });

    public static void OpenSettings()
    {
        var context = Platform.CurrentActivity ?? (Context)Application.Context;
        var intent = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Intent(global::Android.Provider.Settings.ActionAppNotificationSettings)
                .PutExtra(global::Android.Provider.Settings.ExtraAppPackage, context.PackageName)
            : new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings)
                .SetData(global::Android.Net.Uri.Parse("package:" + context.PackageName));
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }
}
#endif
