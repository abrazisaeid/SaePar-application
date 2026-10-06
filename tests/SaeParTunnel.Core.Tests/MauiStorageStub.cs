// Only the platform directory provider is replaced; tests exercise the actual
// app store, serializer, cache files, migrations and backup recovery.
namespace Microsoft.Maui.Storage;
internal sealed class FileSystem
{
    public static FileSystem Current { get; } = new();
    public string AppDataDirectory => throw new InvalidOperationException("Tests must supply an isolated storage directory.");
}
