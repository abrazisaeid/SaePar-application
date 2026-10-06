using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public sealed class JsonCacheStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _unreadablePaths = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T?> ReadAsync<T>(string path)
    {
        var info = GetTypeInfo<T>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Exception? lastError = null;
            foreach (var candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                T? value;
                try
                {
                    await using var stream = File.OpenRead(candidate);
                    value = await JsonSerializer.DeserializeAsync(stream, info).ConfigureAwait(false);
                    if (value is null) throw new JsonException("Cache contains null.");
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    lastError = ex;
                    continue;
                }
                if (candidate != path)
                    await WriteCoreAsync(path, value, info, keepBackup: false).ConfigureAwait(false);
                _unreadablePaths.Remove(Path.GetFullPath(path));
                return value;
            }
            if (lastError is not null)
            {
                _unreadablePaths.Add(Path.GetFullPath(path));
                throw new IOException("دادهٔ ذخیره‌شده قابل خواندن نیست؛ فایل‌ها برای بازیابی حفظ شده‌اند.", lastError);
            }
            _unreadablePaths.Remove(Path.GetFullPath(path));
            return default;
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync<T>(string path, T value)
    {
        var info = GetTypeInfo<T>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_unreadablePaths.Contains(Path.GetFullPath(path)))
                throw new IOException("ذخیره متوقف شد تا دادهٔ قبلیِ خوانده‌نشده بازنویسی نشود.");
            await WriteCoreAsync(path, value, info, keepBackup: true).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>() =>
        StorageJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException($"Cache contract not registered: {typeof(T).Name}");

    private static async Task WriteCoreAsync<T>(string path, T value, JsonTypeInfo<T> info, bool keepBackup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                         FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, value, info).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        if (keepBackup && File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(temp, path, overwrite: true);
    }
}
