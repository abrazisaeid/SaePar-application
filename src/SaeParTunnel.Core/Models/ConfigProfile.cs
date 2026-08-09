using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SaeParTunnel.Core.Models;

public sealed class ConfigProfile : INotifyPropertyChanged
{
    private ProfileHealth _health = ProfileHealth.Untested;
    private int? _latencyMs;
    private DateTime? _lastTested;
    private string _testMessage = string.Empty;
    private bool _isSelectedForTest;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ProxyProtocol Protocol { get; set; }
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Encryption { get; set; } = "none";
    public string Network { get; set; } = "raw";
    public string Security { get; set; } = "none";
    public string Sni { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Flow { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string ShortId { get; set; } = string.Empty;
    public string SpiderX { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Authority { get; set; } = string.Empty;
    public string HeaderType { get; set; } = "none";
    public string Mode { get; set; } = string.Empty;
    public string Alpn { get; set; } = string.Empty;
    public bool AllowInsecure { get; set; }
    public int AlterId { get; set; }
    public string Remark { get; set; } = string.Empty;
    public string OriginalUri { get; set; } = string.Empty;
    public string Source { get; set; } = "دستی";
    public DateTime FirstSeen { get; set; } = DateTime.Now;
    public DateTime LastSeen { get; set; } = DateTime.Now;
    public int FailureCount { get; set; }

    public ProfileHealth Health
    {
        get => _health;
        set
        {
            if (_health == value) return;
            _health = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(PickerDisplayText));
        }
    }

    public int? LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (_latencyMs == value) return;
            _latencyMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(PickerDisplayText));
        }
    }

    public DateTime? LastTested
    {
        get => _lastTested;
        set { if (_lastTested != value) { _lastTested = value; OnPropertyChanged(); } }
    }

    public string TestMessage
    {
        get => _testMessage;
        set { if (_testMessage != value) { _testMessage = value; OnPropertyChanged(); } }
    }

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Remark) ? $"{ProtocolText} • {Address}" : Remark;
    [JsonIgnore] public string Endpoint => $"{Address}:{Port}";
    [JsonIgnore] public string ProtocolText => Protocol switch
    {
        ProxyProtocol.Vless => "VLESS",
        ProxyProtocol.Vmess => "VMess",
        ProxyProtocol.Trojan => "Trojan",
        ProxyProtocol.Shadowsocks => "Shadowsocks",
        _ => "Unknown"
    };
    [JsonIgnore] public string HealthText => Health switch
    {
        ProfileHealth.Untested => "تست نشده",
        ProfileHealth.Testing => "در حال تست",
        ProfileHealth.Working => "سالم",
        ProfileHealth.Failed => "ناموفق",
        ProfileHealth.Unsupported => "پشتیبانی‌نشده",
        ProfileHealth.Reachable => "TCP قابل دسترس",
        _ => "-"
    };
    [JsonIgnore] public string LatencyText => LatencyMs is null ? "-" : $"{LatencyMs} ms";
    [JsonIgnore] public string PickerDisplayText => $"{LatencyText}  •  {ProtocolText}  •  {DisplayName}  •  {Endpoint}";
    [JsonIgnore] public string FirstSeenText => FirstSeen.ToString("yyyy/MM/dd HH:mm");

    [JsonIgnore]
    public bool IsSelectedForTest
    {
        get => _isSelectedForTest;
        set
        {
            if (_isSelectedForTest == value) return;
            _isSelectedForTest = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
