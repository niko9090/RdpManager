using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RdpManager.Core;

public enum LogLevel
{
    /// <summary>Dettagli per la diagnosi (host compresi): scritti solo con il log dettagliato attivo.</summary>
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// File di log a rotazione (di serie in %LOCALAPPDATA%\RdpManager\logs): rdpmanager.log, e quando supera
/// <see cref="MaxBytes"/> diventa rdpmanager.1.log, poi .2 … fino a <see cref="MaxFiles"/> file.
/// Regole: mai password; ai livelli Info e superiori le connessioni compaiono solo con un identificativo breve
/// (niente host, utenti o nomi), gli host solo nei messaggi Debug, che si scrivono solo con il log dettagliato.
/// </summary>
public sealed class AppLog
{
    public const string FileName = "rdpmanager.log";

    private readonly object _gate = new();

    public AppLog(string directory, long maxBytes = 1024 * 1024, int maxFiles = 5)
    {
        Directory = directory;
        MaxBytes = Math.Max(1024, maxBytes);
        MaxFiles = Math.Clamp(maxFiles, 1, 50);
    }

    public string Directory { get; }
    public long MaxBytes { get; }
    public int MaxFiles { get; }

    /// <summary>Con vero si scrivono anche i messaggi Debug (con gli host).</summary>
    public bool Verbose { get; set; }

    public string CurrentPath => Path.Combine(Directory, FileName);

    /// <summary>Cartella predefinita: %LOCALAPPDATA%\RdpManager\logs.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpManager", "logs");

    /// <summary>Log dell'app; null finché il programma non lo crea (le prove automatiche ne fanno a meno).</summary>
    public static AppLog? Default { get; set; }

    /// <summary>Identificativo breve e anonimo di una connessione per i log ("c-1a2b3c4d").</summary>
    public static string Ref(RdpConnection c) => "c-" + c.Id.ToString("N")[..8];

    public void Write(LogLevel level, string message)
    {
        if (level == LogLevel.Debug && !Verbose)
            return;
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{LevelName(level)}] {Redact(message)}";
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line) + 2);
                File.AppendAllText(CurrentPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Il log non deve mai far fallire l'app.
            }
        }
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warning(string message) => Write(LogLevel.Warning, message);

    /// <summary>Errore: tipo e codice dell'eccezione; il testo (che può contenere host o percorsi) solo nel Debug.</summary>
    public void Error(string message, Exception? ex = null)
    {
        Write(LogLevel.Error, ex is null ? message : $"{message} ({ex.GetType().Name}, 0x{ex.HResult:X8})");
        if (ex is not null)
            Debug($"{message}: {ex}");
    }

    /// <summary>Evento di una connessione: al livello indicato solo l'identificativo breve, nel Debug anche l'host.</summary>
    public void Connection(LogLevel level, string action, RdpConnection c, string? detail = null)
    {
        Write(level, $"{action} {Ref(c)}" + (detail is null ? "" : $" — {detail}"));
        Debug($"{action} {Ref(c)} = {RdpFile.FormatAddress(c.Host, c.Port)}");
    }

    private void RotateIfNeeded(long incoming)
    {
        var current = new FileInfo(CurrentPath);
        if (!current.Exists || current.Length + incoming <= MaxBytes)
            return;
        string Numbered(int i) => Path.Combine(Directory, $"rdpmanager.{i}.log");
        var oldest = Numbered(MaxFiles - 1);
        if (MaxFiles == 1)
        {
            File.Delete(CurrentPath);
            return;
        }
        if (File.Exists(oldest))
            File.Delete(oldest);
        for (int i = MaxFiles - 2; i >= 1; i--)
            if (File.Exists(Numbered(i)))
                File.Move(Numbered(i), Numbered(i + 1));
        File.Move(CurrentPath, Numbered(1));
    }

    /// <summary>File di log presenti, dal più recente.</summary>
    public IReadOnlyList<string> Files()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        var list = new List<string>();
        if (File.Exists(CurrentPath))
            list.Add(CurrentPath);
        for (int i = 1; i < MaxFiles; i++)
        {
            var p = Path.Combine(Directory, $"rdpmanager.{i}.log");
            if (File.Exists(p))
                list.Add(p);
        }
        return list;
    }

    private static readonly Regex Secrets = new(
        @"(?i)(password|passwd|pwd|pass|clear\s*text\s*password|protectedpassword)(\s*[:=]\s*|\s+)(""[^""]*""|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Ultima difesa: «password=…», «pwd: …» e simili diventano «password=***».</summary>
    public static string Redact(string? message)
    {
        var text = (message ?? "").ReplaceLineEndings(" ⏎ ");
        return Secrets.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "***");
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warning => "AVVISO",
        _ => "ERRORE",
    };
}
