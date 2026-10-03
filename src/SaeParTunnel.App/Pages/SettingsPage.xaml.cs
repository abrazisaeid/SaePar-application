using SaeParTunnel.App.ViewModels;
namespace SaeParTunnel.App.Pages;
public partial class SettingsPage : ContentPage
{
    private bool _navigating;
    private readonly MainViewModel _vm;
    public SettingsPage(MainViewModel vm) { InitializeComponent(); _vm = vm; BindingContext = vm; }
    protected override async void OnAppearing() { base.OnAppearing(); await _vm.InitializeAsync(); }
    private async void OpenAdvancedPage(object sender, EventArgs e)
    {
        if (_navigating || sender is not Button { CommandParameter: string route }) return;
        _navigating = true;
        try { await Shell.Current.GoToAsync(route); }
        finally { _navigating = false; }
    }
}
