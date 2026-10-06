using SaeParTunnel.App.Services;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.App.ViewModels;

public sealed class HomeServerViewModel : ObservableObject
{
    private bool _isSelected;
    public HomeServerViewModel(ConfigProfile profile, int number) { Profile = profile; Number = number; }
    public ConfigProfile Profile { get; }
    public int Number { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string Name => $"{Number}. {Profile.DisplayName}";
    public string SelectionGlyph => IsSelected ? "●" : "○";
    public string TestStatus => Profile.Health switch
    {
        ProfileHealth.Failed => "آخرین تست پاسخ نداد؛ امکان تلاش دوباره هست",
        ProfileHealth.Reachable => "اینترنت در آخرین تست تأیید نشد",
        ProfileHealth.Testing => "در حال بررسی دوباره",
        _ => Profile.LastTested is { } tested ? $"آخرین تست: {tested:MM/dd HH:mm}" : "ذخیره‌شده"
    };
    public string Ping => Profile.LatencyMs is { } ms ? $"{ms} ms"
        : Profile.LastSuccessfulLatencyMs is { } previous ? $"قبلی: {previous} ms" : "—";
    public void RefreshSelection(bool selected)
    {
        IsSelected = selected;
        OnPropertyChanged(nameof(SelectionGlyph));
    }
}
