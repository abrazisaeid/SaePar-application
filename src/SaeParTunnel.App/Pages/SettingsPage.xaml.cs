using SaeParTunnel.App.ViewModels;
namespace SaeParTunnel.App.Pages;
public partial class SettingsPage : ContentPage { public SettingsPage(MainViewModel vm) { InitializeComponent(); BindingContext = vm; } }
