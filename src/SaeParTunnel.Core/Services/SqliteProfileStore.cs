using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

// Each row keeps the complete source-generated profile contract. A ping changes
// one row; imports and cleanup reconcile a snapshot in a single transaction.
public sealed class SqliteProfileStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _saved;
    private bool _readFailed;
    private static string Fingerprint(string data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    private static Dictionary<string, string> Fingerprints(Dictionary<string, string> rows) =>
        rows.ToDictionary(row => row.Key, row => Fingerprint(row.Value), StringComparer.Ordinal);

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; " +
                "CREATE TABLE IF NOT EXISTS profiles (id TEXT PRIMARY KEY, data TEXT NOT NULL); " +
                "CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static bool IsReady(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key='profiles-schema'";
        var version = command.ExecuteScalar() as string;
        if (version is not null && version != "1") throw new IOException("نسخهٔ بانک سرورها پشتیبانی نمی‌شود؛ داده‌ها حفظ شدند.");
        return version == "1";
    }

    private static Dictionary<string, string> ReadRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, data FROM profiles";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) rows.Add(reader.GetString(0), reader.GetString(1));
        return rows;
    }

    public Task<List<ConfigProfile>?> LoadAsync() => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = Open();
            if (!IsReady(connection)) { _readFailed = false; return null; }
            var rows = ReadRows(connection);
            var profiles = rows.Select(row =>
            {
                var profile = JsonSerializer.Deserialize(row.Value, StorageJsonContext.Default.ConfigProfile)
                    ?? throw new IOException("رکورد سرور قابل خواندن نیست؛ داده‌ها حفظ شدند.");
                if (profile.Id != row.Key) throw new IOException("شناسهٔ رکورد سرور با دادهٔ ذخیره‌شده سازگار نیست.");
                return profile;
            }).ToList();
            _saved = Fingerprints(rows);
            _readFailed = false;
            return profiles;
        }
        catch { _readFailed = true; throw; }
        finally { _gate.Release(); }
    });

    public Task<int> SaveSnapshotAsync(IEnumerable<ConfigProfile> profiles)
    {
        var snapshot = profiles.ToList();
        return Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_readFailed) throw new IOException("ذخیره متوقف شد تا بانک خوانده‌نشده بازنویسی نشود.");
                using var connection = Open();
                var ready = IsReady(connection);
                var prior = _saved ?? (ready ? Fingerprints(ReadRows(connection)) : new Dictionary<string, string>(StringComparer.Ordinal));
                var next = new Dictionary<string, string>(StringComparer.Ordinal);
                using var transaction = connection.BeginTransaction();
                var changed = 0;
                foreach (var profile in snapshot)
                {
                    var data = JsonSerializer.Serialize(profile, StorageJsonContext.Default.ConfigProfile);
                    var fingerprint = Fingerprint(data);
                    next.Add(profile.Id, fingerprint);
                    if (prior.TryGetValue(profile.Id, out var old) && old == fingerprint) continue;
                    Upsert(connection, transaction, profile.Id, data);
                    changed++;
                }
                foreach (var id in prior.Keys.Where(id => !next.ContainsKey(id)))
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM profiles WHERE id=$id";
                    command.Parameters.AddWithValue("$id", id);
                    changed += command.ExecuteNonQuery();
                }
                using (var marker = connection.CreateCommand())
                {
                    marker.Transaction = transaction;
                    marker.CommandText = "INSERT OR REPLACE INTO metadata VALUES ('profiles-schema','1')";
                    marker.ExecuteNonQuery();
                }
                transaction.Commit();
                _saved = next;
                return changed;
            }
            finally { _gate.Release(); }
        });
    }

    public Task SaveProfileAsync(ConfigProfile profile)
    {
        // Capture before awaiting: subsequent UI changes cannot alter this write.
        var id = profile.Id;
        var data = JsonSerializer.Serialize(profile, StorageJsonContext.Default.ConfigProfile);
        return Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_readFailed) throw new IOException("ذخیره متوقف شد تا بانک خوانده‌نشده بازنویسی نشود.");
                using var connection = Open();
                if (!IsReady(connection)) throw new IOException("بانک سرورها هنوز کامل بازیابی نشده است.");
                var prior = _saved ??= Fingerprints(ReadRows(connection));
                if (!prior.ContainsKey(id)) throw new IOException("سرور حذف‌شده با نتیجهٔ تست دوباره اضافه نمی‌شود.");
                var fingerprint = Fingerprint(data);
                if (prior[id] == fingerprint) return;
                using var transaction = connection.BeginTransaction();
                Upsert(connection, transaction, id, data);
                transaction.Commit();
                prior[id] = fingerprint;
            }
            finally { _gate.Release(); }
        });
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, string id, string data)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO profiles (id,data) VALUES ($id,$data) " +
            "ON CONFLICT(id) DO UPDATE SET data=excluded.data";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }
}
