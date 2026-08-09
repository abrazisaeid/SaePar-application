#if IOS
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace SaeParTunnel.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
#endif
