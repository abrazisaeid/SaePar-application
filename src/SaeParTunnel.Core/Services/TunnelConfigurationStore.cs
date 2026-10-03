namespace SaeParTunnel.Core.Services;

// Pass only an opaque token through Android Binder. The multi-megabyte routing
// configuration stays in the app's private cache, never in an Intent parcel.
public sealed class TunnelConfigurationStore(string directory)
{
    public async Task<string> WriteAsync(string json, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var token = Guid.NewGuid().ToString("N");
        var path = GetPath(token);
        try
        {
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            return token;
        }
        catch
        {
            Delete(token);
            throw;
        }
    }

    public async Task<string> ConsumeAsync(string token)
    {
        var path = GetPath(token);
        try { return await File.ReadAllTextAsync(path).ConfigureAwait(false); }
        finally { Delete(token); }
    }

    public void Delete(string token)
    {
        var path = GetPath(token);
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string GetPath(string token)
    {
        if (token.Length != 32 || !Guid.TryParseExact(token, "N", out _))
            throw new ArgumentException("Invalid tunnel configuration token.", nameof(token));
        return Path.Combine(directory, $"tunnel-{token}.json");
    }
}
