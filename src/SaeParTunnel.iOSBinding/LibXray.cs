using System.Runtime.InteropServices;

namespace SaeParTunnel.iOSBinding;

public static class LibXray
{
    private const string InternalLibrary = "__Internal";

    public static string Invoke(string requestJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestJson);

        var requestPointer = Marshal.StringToCoTaskMemUTF8(requestJson);
        try
        {
            var responsePointer = CGoInvoke(requestPointer);
            if (responsePointer == IntPtr.Zero)
                throw new InvalidOperationException("libXray returned a null response.");

            try
            {
                return Marshal.PtrToStringUTF8(responsePointer)
                    ?? throw new InvalidOperationException("libXray returned invalid UTF-8.");
            }
            finally
            {
                CGoFree(responsePointer);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(requestPointer);
        }
    }

    [DllImport(InternalLibrary, EntryPoint = "CGoInvoke", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CGoInvoke(IntPtr requestJson);

    [DllImport(InternalLibrary, EntryPoint = "CGoFree", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CGoFree(IntPtr value);
}
