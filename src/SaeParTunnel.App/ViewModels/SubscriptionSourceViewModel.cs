using SaeParTunnel.App.Services;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.App.ViewModels;

public sealed class SubscriptionSourceViewModel : ObservableObject
{
    private readonly Action _sourceChanged;
    private bool _isExpanded;

    public SubscriptionSourceViewModel(SubscriptionSource source, Action sourceChanged, bool isExpanded = false)
    {
        Source = source;
        _sourceChanged = sourceChanged;
        _isExpanded = isExpanded;
    }

    public SubscriptionSource Source { get; }
    public string Name => Source.Name;
    public string Url => Source.Url;
    public bool CanRemove => !Source.IsBuiltIn;

    public bool IsEnabled
    {
        get => Source.IsEnabled;
        set
        {
            if (Source.IsEnabled == value) return;
            Source.IsEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
            _sourceChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(ExpansionGlyph));
        }
    }

    public string ExpansionGlyph => IsExpanded ? "⌃" : "⌄";
    public string StateText => IsEnabled ? $"فعال • {Host}" : $"غیرفعال • {Host}";
    public string LastFetchedText => Source.LastFetchedUtc is null
        ? "هنوز از این منبع دریافت نشده است."
        : $"آخرین دریافت: {Source.LastFetchedUtc.Value.ToLocalTime():yyyy/MM/dd HH:mm}";

    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    public void Expand() => IsExpanded = true;

    public void RefreshFetchMetadata()
    {
        OnPropertyChanged(nameof(LastFetchedText));
        OnPropertyChanged(nameof(StateText));
    }

    private string Host => Uri.TryCreate(Source.Url, UriKind.Absolute, out var uri)
        ? uri.Host
        : "آدرس نامعتبر";
}
