using System.Runtime.InteropServices;
using System.Text;
using ObjCRuntime;

namespace SaeParTunnel.iOS.Extension;

internal static class UtunFileDescriptor
{
    private const int SystemProtocolControl = 2;
    private const int UtunInterfaceNameOption = 2;
    private const int InterfaceNameSize = 16;
    private const int MaximumDescriptor = 1024;
    private static readonly byte[] UtunPrefix = Encoding.ASCII.GetBytes("utun");

    public static int Find()
    {
        var interfaceName = new byte[InterfaceNameSize];
        for (var descriptor = 0; descriptor <= MaximumDescriptor; descriptor++)
        {
            Array.Clear(interfaceName);
            uint length = InterfaceNameSize;
            if (GetSocketOption(
                    descriptor,
                    SystemProtocolControl,
                    UtunInterfaceNameOption,
                    interfaceName,
                    ref length) == 0 &&
                length >= UtunPrefix.Length &&
                interfaceName.AsSpan(0, UtunPrefix.Length).SequenceEqual(UtunPrefix))
            {
                return descriptor;
            }
        }

        throw new InvalidOperationException("NetworkExtension did not expose a utun file descriptor.");
    }

    [DllImport(Constants.SystemLibrary, EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetSocketOption(
        int socket,
        int level,
        int optionName,
        [Out] byte[] optionValue,
        ref uint optionLength);
}
