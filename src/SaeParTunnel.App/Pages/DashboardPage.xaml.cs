using SaeParTunnel.App.ViewModels;
namespace SaeParTunnel.App.Pages;
public partial class DashboardPage : ContentPage
{
    private readonly MainViewModel _vm;
    public DashboardPage(MainViewModel vm) { InitializeComponent(); _vm = vm; BindingContext = vm; }
    protected override async void OnAppearing() { base.OnAppearing(); await _vm.InitializeAsync(); }
}
