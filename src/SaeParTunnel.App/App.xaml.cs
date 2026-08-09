using Microsoft.Extensions.DependencyInjection;

namespace SaeParTunnel.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        // IMPORTANT: load application-level ResourceDictionaries before resolving
        // AppShell/pages from DI. Pages use StaticResource keys from App.xaml.
        InitializeComponent();
        _services = services;
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var shell = _services.GetRequiredService<AppShell>();
        return new Window(shell)
        {
            Title = "SaePar Tunnel"
        };
    }
}
