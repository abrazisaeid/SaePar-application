using System.Text;

namespace SaeParTunnel.Core.Services;

public static class Base64Url
{
    public static byte[] Decode(string value)
    {
        value = value.Trim().Replace('-', '+').Replace('_', '/');
        var pad = value.Length % 4;
        if (pad != 0) value = value.PadRight(value.Length + (4 - pad), '=');
        return Convert.FromBase64String(value);
    }

    public static string DecodeToString(string value) => Encoding.UTF8.GetString(Decode(value));
}
