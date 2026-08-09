using SaeParTunnel.App.ViewModels;
namespace SaeParTunnel.App.Pages;
public partial class ConfigsPage : ContentPage { public ConfigsPage(MainViewModel vm) { InitializeComponent(); BindingContext = vm; } }
