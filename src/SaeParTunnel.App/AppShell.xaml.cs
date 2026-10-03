using SaeParTunnel.App.Pages;

namespace SaeParTunnel.App;

public partial class AppShell : Shell
{
    public AppShell(DashboardPage dashboard, SettingsPage settings)
    {
        InitializeComponent();

        var tabs = new TabBar();
        tabs.Items.Add(new ShellContent { Title = "اتصال", Route = "dashboard", Content = dashboard });
        tabs.Items.Add(new ShellContent { Title = "پیشرفته", Route = "settings", Content = settings });
        Items.Add(tabs);
        Routing.RegisterRoute("advanced-configs", typeof(ConfigsPage));
        Routing.RegisterRoute("advanced-diagnostics", typeof(DiagnosticsPage));
        Routing.RegisterRoute("advanced-whitelist", typeof(WhitelistPage));
    }
}
