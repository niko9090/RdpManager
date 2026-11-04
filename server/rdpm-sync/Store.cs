using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RdpmSync;

public sealed record Account(long Id, string Username, byte[] Hash, byte[] Salt, int Iterations, bool Disabled);

public sealed record TokenInfo(long TokenId, long AccountId, string Username, DateTimeOffset ExpiresUtc);

public sealed record DocumentInfo(long Version, byte[] Blob, DateTimeOffset UpdatedUtc);

public enum PutResult { Ok, Conflict }

public sealed record VpnDevice(long Id, long AccountId, string Name, string PublicKey, string Address, DateTimeOffset CreatedUtc);

/// <summary>Database SQLite: account, token (se ne salva solo l'hash) e un documento cifrato per account.</summary>
public sealed class Store
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;

    public Store(string path, TimeProvider time)
    {
        _time = time;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS accounts(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              username TEXT NOT NULL UNIQUE COLLATE NOCASE,
              hash BLOB NOT NULL, salt BLOB NOT NULL, iterations INTEGER NOT NULL,
              disabled INTEGER NOT NULL DEFAULT 0,
              created_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS tokens(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
              token_hash TEXT NOT NULL UNIQUE,
              label TEXT,
              created_utc INTEGER NOT NULL, expires_utc INTEGER NOT NULL,
              revoked INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS documents(
              account_id INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
              version INTEGER NOT NULL, blob BLOB NOT NULL, updated_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS vpn_devices(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
              name TEXT NOT NULL COLLATE NOCASE,
              public_key TEXT NOT NULL UNIQUE,
              address TEXT NOT NULL UNIQUE,
              created_utc INTEGER NOT NULL,
              UNIQUE(account_id, name));
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Now => _time.GetUtcNow().ToUnixTimeSeconds();

    public static bool ValidUsername(string? u) =>
        u is { Length: >= 3 and <= 64 } && u.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '@');

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Crea l'account; false se il nome esiste già.</summary>
    public bool CreateAccount(string username, string secret, int iterations)
    {
        var (hash, salt) = PasswordHasher.Hash(secret, iterations);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts(username, hash, salt, iterations, created_utc) VALUES($u,$h,$s,$i,$t)
            ON CONFLICT(username) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$i", iterations);
        cmd.Parameters.AddWithValue("$t", Now);
        return cmd.ExecuteNonQuery() == 1;
    }

    public Account? FindAccount(string username)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, username, hash, salt, iterations, disabled FROM accounts WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new Account(r.GetInt64(0), r.GetString(1), (byte[])r["hash"], (byte[])r["salt"], r.GetInt32(4), r.GetInt64(5) != 0)
            : null;
    }

    public List<(string Username, DateTimeOffset Created, bool Disabled, bool HasDocument)> ListAccounts()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT a.username, a.created_utc, a.disabled, EXISTS(SELECT 1 FROM documents d WHERE d.account_id = a.id)
            FROM accounts a ORDER BY a.username
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<(string, DateTimeOffset, bool, bool)>();
        while (r.Read())
            list.Add((r.GetString(0), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)), r.GetInt64(2) != 0, r.GetInt64(3) != 0));
        return list;
    }

    /// <summary>Elimina l'account con token e documento.</summary>
    public bool DeleteAccount(string username)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM accounts WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        return cmd.ExecuteNonQuery() == 1;
    }

    public (string Token, DateTimeOffset Expires) IssueToken(long accountId, TimeSpan lifetime, string? label)
    {
        var token = "rdpm_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = _time.GetUtcNow() + lifetime;
        using var c = Open();
        Exec(c, $"DELETE FROM tokens WHERE expires_utc < {Now - 86400}");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO tokens(account_id, token_hash, label, created_utc, expires_utc) VALUES($a,$h,$l,$c,$e)";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$h", HashToken(token));
        cmd.Parameters.AddWithValue("$l", (object?)label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", Now);
        cmd.Parameters.AddWithValue("$e", expires.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
        return (token, DateTimeOffset.FromUnixTimeSeconds(expires.ToUnixTimeSeconds()));
    }

    /// <summary>Token valido: non scaduto, non revocato, account non disattivato.</summary>
    public TokenInfo? ValidateToken(string token)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.account_id, a.username, t.expires_utc FROM tokens t JOIN accounts a ON a.id = t.account_id
            WHERE t.token_hash = $h AND t.revoked = 0 AND t.expires_utc > $now AND a.disabled = 0
            """;
        cmd.Parameters.AddWithValue("$h", HashToken(token));
        cmd.Parameters.AddWithValue("$now", Now);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new TokenInfo(r.GetInt64(0), r.GetInt64(1), r.GetString(2), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)))
            : null;
    }

    public void RevokeToken(long tokenId) => Run("UPDATE tokens SET revoked = 1 WHERE id = $x", tokenId);

    public int RevokeAllTokens(long accountId) => Run("UPDATE tokens SET revoked = 1 WHERE account_id = $x AND revoked = 0", accountId);

    private int Run(string sql, long arg)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$x", arg);
        return cmd.ExecuteNonQuery();
    }

    public DocumentInfo? GetDocument(long accountId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version, blob, updated_utc FROM documents WHERE account_id = $a";
        cmd.Parameters.AddWithValue("$a", accountId);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new DocumentInfo(r.GetInt64(0), (byte[])r["blob"], DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)))
            : null;
    }

    /// <summary>
    /// Scrive il documento solo se la versione attuale è <paramref name="expectedVersion"/> (0 = non esiste ancora):
    /// un'unica istruzione SQL, quindi due scritture insieme non si sovrascrivono.
    /// </summary>
    public PutResult PutDocument(long accountId, long expectedVersion, byte[] blob, out long newVersion)
    {
        newVersion = expectedVersion + 1;
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (expectedVersion == 0)
            cmd.CommandText = """
                INSERT INTO documents(account_id, version, blob, updated_utc) VALUES($a, 1, $b, $t)
                ON CONFLICT(account_id) DO NOTHING
                """;
        else
        {
            cmd.CommandText = "UPDATE documents SET version = $v + 1, blob = $b, updated_utc = $t WHERE account_id = $a AND version = $v";
            cmd.Parameters.AddWithValue("$v", expectedVersion);
        }
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$b", blob);
        cmd.Parameters.AddWithValue("$t", Now);
        return cmd.ExecuteNonQuery() == 1 ? PutResult.Ok : PutResult.Conflict;
    }

    // ---------- VPN: dispositivi (peer WireGuard) ----------

    private static VpnDevice ReadDevice(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)));

    private const string DeviceColumns = "id, account_id, name, public_key, address, created_utc";

    public List<VpnDevice> ListDevices(long? accountId = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {DeviceColumns} FROM vpn_devices WHERE $a IS NULL OR account_id = $a ORDER BY id";
        cmd.Parameters.AddWithValue("$a", (object?)accountId ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<VpnDevice>();
        while (r.Read())
            list.Add(ReadDevice(r));
        return list;
    }

    public VpnDevice? FindDevice(long accountId, string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {DeviceColumns} FROM vpn_devices WHERE account_id = $a AND name = $n";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDevice(r) : null;
    }

    public VpnDevice? FindDeviceByKey(string publicKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {DeviceColumns} FROM vpn_devices WHERE public_key = $k";
        cmd.Parameters.AddWithValue("$k", publicKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDevice(r) : null;
    }

    public void InsertDevice(long accountId, string name, string publicKey, string address)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO vpn_devices(account_id, name, public_key, address, created_utc) VALUES($a,$n,$k,$ad,$t)";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$k", publicKey);
        cmd.Parameters.AddWithValue("$ad", address);
        cmd.Parameters.AddWithValue("$t", Now);
        cmd.ExecuteNonQuery();
    }

    public void ReplaceDeviceKey(long deviceId, string publicKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE vpn_devices SET public_key = $k WHERE id = $i";
        cmd.Parameters.AddWithValue("$k", publicKey);
        cmd.Parameters.AddWithValue("$i", deviceId);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteDevice(long deviceId) => Run("DELETE FROM vpn_devices WHERE id = $x", deviceId) == 1;
}
