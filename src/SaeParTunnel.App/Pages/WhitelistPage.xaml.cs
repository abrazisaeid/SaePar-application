using SaeParTunnel.App.ViewModels;
using SaeParTunnel.Core.Models;
namespace SaeParTunnel.App.Pages;
public partial class WhitelistPage : ContentPage
{
    private readonly MainViewModel _vm;
    public WhitelistPage(MainViewModel vm) { InitializeComponent(); _vm = vm; BindingContext = vm; }
    private void OnRemoveWebsiteClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: string website }) _vm.RemoveWebsiteCommand.Execute(website);
    }
    private void OnRemoveApplicationClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: WhitelistApplication app }) _vm.RemoveApplicationCommand.Execute(app);
    }
}
