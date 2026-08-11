using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace SaeParTunnel.App.Pages;

public partial class ProfileSharePage : ContentPage
{
    private readonly byte[] _qrPng;

    public ProfileSharePage(string profileName, string profileDetails, string shareText, byte[] qrPng)
    {
        InitializeComponent();
        ProfileName = profileName;
        ProfileDetails = profileDetails;
        ShareText = shareText;
        _qrPng = qrPng;
        QrImage = ImageSource.FromStream(() => new MemoryStream(_qrPng, writable: false));
        BindingContext = this;
    }

    public string ProfileName { get; }
    public string ProfileDetails { get; }
    public string ShareText { get; }
    public ImageSource QrImage { get; }

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        if (Width <= 0) return;
        var size = Math.Clamp(Width - 72, 210, 360);
        QrImageView.WidthRequest = size;
        QrImageView.HeightRequest = size;
    }

    private async void OnCopyTextClicked(object? sender, EventArgs e) =>
        await RunSafeAsync(async () =>
        {
            await Clipboard.Default.SetTextAsync(ShareText);
            ShareStatusLabel.Text = "متن کانفیگ کپی شد.";
        });

    private async void OnShareTextClicked(object? sender, EventArgs e) =>
        await RunSafeAsync(async () =>
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = $"کانفیگ {ProfileName}",
                Text = ShareText
            });
        }, copyTextOnFailure: true);

    private async void OnShareQrClicked(object? sender, EventArgs e) =>
        await RunSafeAsync(async () =>
        {
            var path = Path.Combine(FileSystem.CacheDirectory, $"saepar-qr-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(path, _qrPng);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = $"QR کانفیگ {ProfileName}",
                File = new ShareFile(path, "image/png")
            });
        });

    private async void OnCloseClicked(object? sender, EventArgs e) =>
        await Navigation.PopModalAsync();

    private async Task RunSafeAsync(Func<Task> action, bool copyTextOnFailure = false)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            if (copyTextOnFailure && await TryCopyTextAsync())
            {
                ShareStatusLabel.Text = "اشتراک سیستمی در دسترس نبود؛ متن کانفیگ کپی شد.";
                return;
            }

            ShareStatusLabel.Text = "اشتراک‌گذاری انجام نشد. دوباره تلاش کن.";
        }
    }

    private async Task<bool> TryCopyTextAsync()
    {
        try
        {
            await Clipboard.Default.SetTextAsync(ShareText);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
