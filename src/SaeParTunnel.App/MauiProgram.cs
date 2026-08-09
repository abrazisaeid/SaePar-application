using Microsoft.Extensions.DependencyInjection;
using SaeParTunnel.App.Pages;
using SaeParTunnel.App.Services;
using SaeParTunnel.App.ViewModels;
using SaeParTunnel.Core.Abstractions;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton<ConfigParser>();
        builder.Services.AddSingleton<ConfigExtractor>();
        builder.Services.AddSingleton<GitHubConfigService>();
        builder.Services.AddSingleton<XrayConfigBuilder>();
        builder.Services.AddSingleton<EndpointPrecheckService>();
        builder.Services.AddSingleton<MauiJsonStore>();

#if WINDOWS
        builder.Services.AddSingleton<ITunnelService, Platforms.Windows.WindowsTunnelService>();
#elif ANDROID
        builder.Services.AddSingleton<ITunnelService, Platforms.Android.AndroidTunnelService>();
#elif IOS
        builder.Services.AddSingleton<ITunnelService, Platforms.iOS.IosTunnelService>();
#else
        builder.Services.AddSingleton<ITunnelService, Services.UnsupportedTunnelService>();
#endif

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<DashboardPage>();
        builder.Services.AddSingleton<ConfigsPage>();
        builder.Services.AddSingleton<WhitelistPage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<AppShell>();
        return builder.Build();
    }
}
