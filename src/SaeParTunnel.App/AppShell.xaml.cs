using SaeParTunnel.App.Pages;

namespace SaeParTunnel.App;

public partial class AppShell : Shell
{
    public AppShell(DashboardPage dashboard, ConfigsPage configs, WhitelistPage whitelist, SettingsPage settings)
    {
        InitializeComponent();

        var tabs = new TabBar();
        tabs.Items.Add(new ShellContent { Title = "خانه", Route = "dashboard", Content = dashboard });
        tabs.Items.Add(new ShellContent { Title = "کانفیگ‌ها", Route = "configs", Content = configs });
        tabs.Items.Add(new ShellContent { Title = "Whitelist", Route = "whitelist", Content = whitelist });
        tabs.Items.Add(new ShellContent { Title = "تنظیمات", Route = "settings", Content = settings });
        Items.Add(tabs);
    }
}
