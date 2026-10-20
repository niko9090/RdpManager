using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Core;

/// <summary>File .rdpm protetto: manca la password del file, oppure è sbagliata.</summary>
public sealed class RdpmPasswordException(string message, bool passwordMissing) : Exception(message)
{
    /// <summary>Vero se il file è cifrato e non è stata indicata nessuna password.</summary>
    public bool PasswordMissing { get; } = passwordMissing;
}

public enum ImportMode
{
    /// <summary>Aggiunge le connessioni nuove; quelle già presenti (stesso Id o stessa destinazione) restano come sono.</summary>
    Merge,
    /// <summary>Sostituisce tutte le connessioni e le cartelle con quelle del file; le impostazioni restano.</summary>
    Replace,
}

/// <param name="Added">Connessioni aggiunte.</param>
/// <param name="Skipped">Doppioni non importati.</param>
/// <param name="Removed">Connessioni tolte (solo in sostituzione).</param>
public sealed record ImportResult(int Added, int Skipped, int Removed);

/// <summary>
/// Esportazione e importazione delle connessioni in un file .rdpm (JSON).
/// Le password non vengono mai scritte in chiaro: si escludono, oppure si cifrano con AES-GCM
/// usando una chiave ricavata dalla password del file con PBKDF2-SHA256.
/// Le password DPAPI di connections.json non si esportano: valgono solo per l'utente Windows che le ha create.
/// </summary>
public static class RdpmFile
{
    public const string Extension = ".rdpm";
    public const string DialogFilter = "Connessioni RdpManager (*.rdpm)|*.rdpm|Tutti i file (*.*)|*.*";
    public const string FormatName = "rdpm";
    public const int FormatVersion = 1;
    public const int DefaultIterations = 600_000;

    private const int SaltSize = 16, KeySize = 32, NonceSize = 12, TagSize = 16;
    private const string CheckText = "RdpManager/rdpm/check";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="filePassword">null o vuota = password escluse; altrimenti le password vengono cifrate con questa.</param>
    public static string Export(IEnumerable<RdpConnection> connections, IEnumerable<string>? groups = null,
        string? filePassword = null, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var file = new RdpmDocument
        {
            Format = FormatName,
            Version = FormatVersion,
            ExportedUtc = DateTime.UtcNow,
            App = $"RdpManager {AppInfo.VersionText}",
            Groups = (groups ?? []).Select(RdpConnection.NormalizeGroup).Where(g => g.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList(),
        };

        byte[]? key = null;
        if (!string.IsNullOrEmpty(filePassword))
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            key = DeriveKey(filePassword, salt, iterations);
            file.Encryption = new RdpmEncryption
            {
                Kdf = "PBKDF2-SHA256",
                Iterations = iterations,
                Salt = Convert.ToBase64String(salt),
                Cipher = "AES-256-GCM",
                Check = Seal(key, Encoding.UTF8.GetBytes(CheckText), "check"),
            };
        }

        try
        {
            foreach (var c in connections)
            {
                var copy = c.Clone();
                copy.Password = null;
                copy.ProtectedPassword = null;
                string? sealedPassword = key is not null && !string.IsNullOrEmpty(c.Password)
                    ? Seal(key, Encoding.UTF8.GetBytes(c.Password), c.Id.ToString())
                    : null;
                file.Connections.Add(new RdpmEntry { Connection = copy, EncryptedPassword = sealedPassword });
            }
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
        return JsonSerializer.Serialize(file, JsonOptions);
    }

    public static void Save(string path, IEnumerable<RdpConnection> connections, IEnumerable<string>? groups = null,
        string? filePassword = null) =>
        File.WriteAllText(path, Export(connections, groups, filePassword), new UTF8Encoding(false));

    /// <summary>Vero se il file contiene password cifrate e quindi serve la password del file.</summary>
    /// <exception cref="InvalidDataException">Il contenuto non è un file .rdpm valido.</exception>
    public static bool IsEncrypted(string json) => ReadDocument(json).Encryption is not null;

    /// <summary>Legge un .rdpm; le password decifrate finiscono in <see cref="RdpConnection.Password"/>.</summary>
    /// <exception cref="RdpmPasswordException">File cifrato e password del file mancante o sbagliata.</exception>
    /// <exception cref="InvalidDataException">Il contenuto non è un file .rdpm valido.</exception>
    public static ConnectionsDocument Import(string json, string? filePassword = null)
    {
        var file = ReadDocument(json);
        byte[]? key = null;
        if (file.Encryption is { } enc)
        {
            if (string.IsNullOrEmpty(filePassword))
                throw new RdpmPasswordException("Il file contiene password cifrate: indica la password del file.", passwordMissing: true);
            if (enc.Iterations is < 1000 or > 10_000_000 || !TryBase64(enc.Salt, out var salt) || salt.Length < 8)
                throw new InvalidDataException("Parametri di cifratura del file .rdpm non validi.");
            key = DeriveKey(filePassword, salt, enc.Iterations);
            if (!TryOpen(key, enc.Check, "check", out var check) || Encoding.UTF8.GetString(check) != CheckText)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new RdpmPasswordException("Password del file sbagliata: nessuna connessione è stata importata.", passwordMissing: false);
            }
        }

        try
        {
            var doc = new ConnectionsDocument { Groups = file.Groups ?? [] };
            foreach (var entry in file.Connections ?? [])
            {
                if (entry?.Connection is not { } c)
                    continue;
                c.ProtectedPassword = null;
                c.Password = null;
                if (entry.EncryptedPassword is { } sealedPassword && key is not null)
                {
                    if (!TryOpen(key, sealedPassword, c.Id.ToString(), out var plain))
                        throw new InvalidDataException($"La password della connessione «{c.DisplayName}» è danneggiata.");
                    c.Password = Encoding.UTF8.GetString(plain);
                }
                c.Group = RdpConnection.NormalizeGroup(c.Group);
                c.Color = RdpConnection.NormalizeColor(c.Color);
                c.Name ??= "";
                c.Host ??= "";
                c.Username ??= "";
                c.Domain ??= "";
                c.Label ??= "";
                c.MacAddress ??= "";
                doc.Connections.Add(c);
            }
            return doc;
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    public static ConnectionsDocument Load(string path, string? filePassword = null) =>
        Import(File.ReadAllText(path), filePassword);

    /// <summary>
    /// Porta in <paramref name="target"/> le connessioni importate.
    /// In unione salta i doppioni (stesso Id o stessa destinazione host/porta/utente/dominio, anche dentro il file stesso);
    /// in sostituzione rimpiazza connessioni e cartelle.
    /// </summary>
    public static ImportResult ApplyImport(ConnectionsDocument target, ConnectionsDocument imported, ImportMode mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(imported);
        int removed = 0;
        if (mode == ImportMode.Replace)
        {
            removed = target.Connections.Count;
            target.Connections = [];
            target.Groups = [];
        }

        int added = 0, skipped = 0;
        foreach (var c in imported.Connections)
        {
            var existing = target.Connections.FirstOrDefault(e => e.Id == c.Id || e.IsSameTarget(c));
            if (existing is not null)
            {
                // Il doppione non si aggiunge, ma se a quella esistente manca la password si prende quella del file.
                if (!existing.HasPassword && !string.IsNullOrEmpty(c.Password))
                    existing.Password = c.Password;
                skipped++;
                continue;
            }
            target.Connections.Add(c);
            added++;
        }
        foreach (var g in imported.Groups)
            target.AddFolder("", g);
        if (mode == ImportMode.Replace)
            target.Settings.OpenSessions.RemoveAll(id => target.Find(id) is null);
        return new ImportResult(added, skipped, removed);
    }

    private static RdpmDocument ReadDocument(string json)
    {
        RdpmDocument? file;
        try
        {
            file = JsonSerializer.Deserialize<RdpmDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Il file non è un'esportazione di RdpManager (.rdpm) valida.", ex);
        }
        if (file is null || !string.Equals(file.Format, FormatName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Il file non è un'esportazione di RdpManager (.rdpm).");
        if (file.Version > FormatVersion)
            throw new InvalidDataException($"Il file .rdpm è di una versione più recente ({file.Version}): aggiorna RdpManager.");
        return file;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>base64(nonce | testo cifrato | tag); <paramref name="context"/> è autenticato ma non cifrato.</summary>
    private static string Seal(byte[] key, byte[] plain, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    private static bool TryOpen(byte[] key, string? sealedBase64, string context, out byte[] plain)
    {
        plain = [];
        if (!TryBase64(sealedBase64, out var data) || data.Length < NonceSize + TagSize)
            return false;
        var result = new byte[data.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, result.Length),
                data.AsSpan(data.Length - TagSize), result, Encoding.UTF8.GetBytes(context));
        }
        catch (CryptographicException)
        {
            return false;
        }
        plain = result;
        return true;
    }

    private static bool TryBase64(string? text, out byte[] data)
    {
        data = [];
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            data = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class RdpmDocument
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public DateTime? ExportedUtc { get; set; }
        public string? App { get; set; }
        public RdpmEncryption? Encryption { get; set; }
        public List<string> Groups { get; set; } = [];
        public List<RdpmEntry> Connections { get; set; } = [];
    }

    private sealed class RdpmEncryption
    {
        public string Kdf { get; set; } = "";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Cipher { get; set; } = "";
        public string Check { get; set; } = "";
    }

    private sealed class RdpmEntry
    {
        public RdpConnection? Connection { get; set; }
        public string? EncryptedPassword { get; set; }
    }
}
