using QRCoder;

namespace SaeParTunnel.App.Services;

public sealed class QrCodeService
{
    public byte[] CreatePng(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("متنی برای ساخت QR وجود ندارد.");

        return PngByteQRCodeHelper.GetQRCode(
            content,
            QRCodeGenerator.ECCLevel.L,
            8);
    }
}
