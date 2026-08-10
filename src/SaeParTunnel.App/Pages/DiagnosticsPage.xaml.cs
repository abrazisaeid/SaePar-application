using SaeParTunnel.App.ViewModels;

namespace SaeParTunnel.App.Pages;

public partial class DiagnosticsPage : ContentPage
{
    private readonly MainViewModel _vm;

    public DiagnosticsPage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.InitializeAsync();
        _vm.RefreshDiagnosticsCommand.Execute(null);
    }
}
