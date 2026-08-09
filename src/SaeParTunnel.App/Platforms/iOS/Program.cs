#if IOS
using UIKit;
namespace SaeParTunnel.App;
public class Program
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
#endif
