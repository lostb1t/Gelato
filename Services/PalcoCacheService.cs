using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// SQLite-backed cache service migrated from Palco.
/// Maintains the same DB location/name for compatibility.
/// </summary>
public class PalcoCacheService : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<PalcoCacheService> _logger;
    private SqliteConnection? _connection;
    private readonly Lock _lock = new();

    public PalcoCacheService(IApplicationPaths appPaths, ILogger<PalcoCacheService> logger)
    {
        _logger = logger;
        // Target existing Palco data folder to preserve legacy data
        var pluginDataPath = Path.Combine(appPaths.DataPath, "Palco");
        Directory.CreateDirectory(pluginDataPath); // Ensure dir exists
        _dbPath = Path.Combine(pluginDataPath, "cache.db");
        Initialize();
    }

    private void Initialize()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        // Ensure table exists
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"
            CREATE TABLE IF NOT EXISTS cache (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                expires_at INTEGER,
                namespace TEXT DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_namespace ON cache(namespace);
        ";
        cmd.ExecuteNonQuery();
        _logger.LogInformation("[Gelato] Palco Cache initialized at {Path}", _dbPath);
    }

    public string? Get(string key, string ns = "")
    {
        lock (_lock)
        {
            if (_connection == null)
                return null;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT value, expires_at FROM cache WHERE key = @key AND namespace = @ns";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@ns", ns);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null; // Not found

            var expiresAt = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Check expiry
            if (expiresAt.HasValue && expiresAt.Value < now)
            {
                Delete(key, ns); // Lazy delete
                return null;
            }

            return reader.GetString(0);
        }
    }

    public void Set(string key, string value, int ttlSeconds = 0, string ns = "")
    {
        lock (_lock)
        {
            if (_connection == null)
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long? expiresAt = ttlSeconds > 0 ? now + ttlSeconds : null;

            // Upsert with new expiry
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                @"
                INSERT OR REPLACE INTO cache (key, value, created_at, expires_at, namespace)
                VALUES (@key, @value, @created, @expires, @ns)
            ";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.Parameters.AddWithValue("@created", now);
            cmd.Parameters.AddWithValue(
                "@expires",
                expiresAt.HasValue ? expiresAt.Value : DBNull.Value
            );
            cmd.Parameters.AddWithValue("@ns", ns);
            cmd.ExecuteNonQuery();
        }
    }

    public bool Delete(string key, string ns = "")
    {
        lock (_lock)
        {
            if (_connection == null)
                return false;

            // Remove specific key in namespace
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cache WHERE key = @key AND namespace = @ns";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@ns", ns);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public Dictionary<string, string> GetBulk(IEnumerable<string> keys, string ns = "")
    {
        var result = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            var value = Get(key, ns);
            if (value != null)
                result[key] = value;
        }
        return result;
    }

    public (int total, int expired, long size) GetStats()
    {
        lock (_lock)
        {
            if (_connection == null)
                return (0, 0, 0);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var cmd1 = _connection.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(*) FROM cache";
            var total = Convert.ToInt32(cmd1.ExecuteScalar());

            using var cmd2 = _connection.CreateCommand();
            cmd2.CommandText =
                "SELECT COUNT(*) FROM cache WHERE expires_at IS NOT NULL AND expires_at < @now";
            cmd2.Parameters.AddWithValue("@now", now);
            var expired = Convert.ToInt32(cmd2.ExecuteScalar());

            var size = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
            return (total, expired, size);
        }
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
