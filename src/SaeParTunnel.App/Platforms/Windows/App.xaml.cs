#if WINDOWS
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
namespace SaeParTunnel.App.WinUI;
public partial class App : MauiWinUIApplication
{
    public App() { InitializeComponent(); }
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
#endif
