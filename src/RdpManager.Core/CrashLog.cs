using System.Globalization;
using System.Text;

namespace RdpManager.Core;

/// <summary>
/// Rete di sicurezza: un'eccezione non gestita (thread dell'interfaccia, altri thread, attività dimenticate) finisce
/// con lo stack completo in <c>crash.log</c>, accanto al log normale, prima che il processo eventualmente si chiuda.
/// Il file non contiene password: l'eccezione è scritta dal sistema (tipo, messaggio, stack) e passa da
/// <see cref="AppLog.Redact"/>. Si ruota da solo (<see cref="MaxBytes"/>, un file di riserva).
/// </summary>
public sealed class CrashLog
{
    public const string FileName = "crash.log";
    public const long MaxBytes = 512 * 1024;

    private readonly object _gate = new();
    private DateTime _lastNotice = DateTime.MinValue;

    /// <summary>Rete di sicurezza dell'app; null finché il programma non la crea (le prove automatiche ne fanno a meno).</summary>
    public static CrashLog? Default { get; set; }

    public CrashLog(string directory) => Directory = directory;

    public string Directory { get; }
    public string CurrentPath => Path.Combine(Directory, FileName);

    /// <summary>Intervallo minimo fra due avvisi all'utente: un errore che si ripete non apre una finestra dopo l'altra.</summary>
    public TimeSpan NoticeInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Scrive l'eccezione con lo stack completo. Non lancia mai.</summary>
    /// <param name="source">Da dove arriva (thread interfaccia, thread non gestito, attività…).</param>
    /// <param name="terminating">Vero se il sistema sta per chiudere il processo.</param>
    public void Write(string source, Exception? ex, bool terminating = false)
    {
        try
        {
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(source).Append(terminating ? ", il programma si chiude" : "").AppendLine("]")
                .AppendLine(AppLog.Redact(ex?.ToString() ?? "(eccezione sconosciuta)").Replace(" ⏎ ", Environment.NewLine))
                .AppendLine();
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var info = new FileInfo(CurrentPath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(CurrentPath, Path.Combine(Directory, "crash.1.log"), overwrite: true);
                File.AppendAllText(CurrentPath, sb.ToString(), new UTF8Encoding(false));
            }
            AppLog.Default?.Error(source, ex); // anche una riga nel log normale, così si vede in ordine con il resto
        }
        catch (Exception)
        {
            // La rete di sicurezza non deve mai diventare essa stessa un errore.
        }
    }

    /// <summary>Vero se è passato abbastanza tempo dall'ultimo avviso per mostrarne un altro (e lo registra).</summary>
    public bool ShouldNotify(DateTime? now = null)
    {
        lock (_gate)
        {
            var t = now ?? DateTime.UtcNow;
            if (t - _lastNotice < NoticeInterval)
                return false;
            _lastNotice = t;
            return true;
        }
    }

    /// <summary>
    /// Collega la rete di sicurezza ai tre punti da cui un'eccezione può uscire: gli eventi sono passati da chi chiama,
    /// così Core non dipende da WinForms. <paramref name="onUiNotice"/> (facoltativo) mostra l'avviso all'utente.
    /// </summary>
    public void Attach(Action<Exception>? onUiNotice = null)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Eccezione non gestita su un thread", e.ExceptionObject as Exception, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Attività finita in errore e mai attesa", e.Exception);
            e.SetObserved();
        };
        UiHandler = ex =>
        {
            Write("Eccezione sul thread dell'interfaccia", ex);
            if (onUiNotice is null || !ShouldNotify())
                return;
            try { onUiNotice(ex); }
            catch (Exception) { /* l'avviso non riuscito non deve far ripartire l'errore */ }
        };
    }

    /// <summary>Gestore per <c>Application.ThreadException</c> (impostato da <see cref="Attach"/>).</summary>
    public Action<Exception> UiHandler { get; private set; } = _ => { };
}
