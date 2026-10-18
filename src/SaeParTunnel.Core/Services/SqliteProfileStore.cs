using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

// Each row keeps the complete source-generated profile contract. A ping changes
// one row; imports and cleanup reconcile a snapshot in a single transaction.
public sealed class SqliteProfileStore(string path, bool maintainBackup = true)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _saved;
    private bool _readFailed;
    public bool IsCorrupt { get; private set; }
    private DateTime _lastBackupUtc;
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
            IsCorrupt = false;
            using var connection = Open();
            if (!IsReady(connection)) { _readFailed = false; return null; }
            var rows = ReadRows(connection);
            var profiles = DeserializeRows(rows);
            _saved = Fingerprints(rows);
            _readFailed = false;
            IsCorrupt = false;
            BackupIfDue(connection);
            return profiles;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        { _readFailed = true; IsCorrupt = true; throw; }
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
                BackupIfDue(connection);
                return changed;
            }
            finally { _gate.Release(); }
        });
    }

    private static List<ConfigProfile> DeserializeRows(Dictionary<string, string> rows) => rows.Select(row =>
    {
        var profile = JsonSerializer.Deserialize(row.Value, StorageJsonContext.Default.ConfigProfile)
            ?? throw new IOException("رکورد سرور قابل خواندن نیست؛ داده‌ها حفظ شدند.");
        if (profile.Id != row.Key) throw new IOException("شناسهٔ رکورد سرور با دادهٔ ذخیره‌شده سازگار نیست.");
        return profile;
    }).ToList();

    private void BackupIfDue(SqliteConnection source)
    {
        if (!maintainBackup || DateTime.UtcNow - _lastBackupUtc < TimeSpan.FromMinutes(5)) return;
        var temp = path + ".backup-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var destination = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }
            File.Move(temp, path + ".bak", overwrite: true);
            _lastBackupUtc = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        { /* An unavailable optional backup cannot invalidate a committed archive. */ }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public Task<List<ConfigProfile>?> ReadRecoveryBackupAsync() => Task.Run(() =>
    {
        if (!File.Exists(path + ".bak")) return null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path + ".bak", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            return IsReady(connection) ? DeserializeRows(ReadRows(connection)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
        { return null; }
    });

    // Only a proven corrupt SQLite file can enter this path. A lock, future
    // schema or a malformed row never authorizes replacement of the archive.
    public async Task RecoverAsync(IReadOnlyList<ConfigProfile> profiles)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var replacement = path + ".recovery-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!IsCorrupt || !_readFailed || profiles.Count == 0)
                throw new IOException("بازیابی فقط با نسخهٔ سالم و بانک آسیب‌دیده انجام می‌شود.");
            var restored = new SqliteProfileStore(replacement, maintainBackup: false);
            await restored.SaveSnapshotAsync(profiles).ConfigureAwait(false);
            var checkedRows = await restored.LoadAsync().ConfigureAwait(false);
            if (checkedRows?.Count != profiles.Count) throw new IOException("تأیید نسخهٔ بازیابی انجام نشد.");
            var quarantine = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
            File.Move(path, quarantine);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Move(path + suffix, quarantine + suffix);
            File.Move(replacement, path);
            _saved = null;
            _readFailed = false;
            IsCorrupt = false;
        }
        finally { _gate.Release(); }
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
