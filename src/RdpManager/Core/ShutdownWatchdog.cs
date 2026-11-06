using System.Diagnostics;

namespace RdpManager.Core;

/// <summary>
/// Cane da guardia della chiusura: parte quando l'uscita è decisa e gira su un thread suo, mai sul thread
/// dell'interfaccia. Se il programma è ancora vivo allo scadere del tempo (il thread dell'interfaccia è bloccato in una
/// chiusura che non finisce: controllo RDP, rete, VPN, …) esce comunque, invece di lasciare la finestra bianca e ferma.
/// </summary>
public sealed class ShutdownWatchdog
{
    /// <summary>Tempo concesso alla chiusura normale prima dell'uscita forzata.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly TimeSpan _timeout;
    private readonly Action _exit;
    private readonly Action _kill;
    private readonly Action<string>? _log;
    private int _state; // 0 = in attesa, 1 = fermato, 2 = scattato

    private readonly TimeSpan _killDelay;

    private ShutdownWatchdog(TimeSpan timeout, Action exit, Action kill, Action<string>? log, TimeSpan killDelay)
    {
        _killDelay = killDelay;
        _timeout = timeout;
        _exit = exit;
        _kill = kill;
        _log = log;
    }

    /// <summary>
    /// Vero solo nel programma vero (lo imposta <c>Program.Main</c>): negli altri processi (prove automatiche, che aprono
    /// e chiudono molte finestre) l'uscita e la terminazione di serie non fanno niente, così non portano via il processo.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>Uscita normale del processo (<c>Environment.Exit</c>) e, se non basta, terminazione (<c>Kill</c>).</summary>
    public static Action DefaultExit { get; } = () =>
    {
        if (Enabled)
            Environment.Exit(0);
    };

    public static Action DefaultKill { get; } = () =>
    {
        if (!Enabled)
            return;
        using var self = Process.GetCurrentProcess();
        self.Kill();
    };

    /// <summary>Tempo fra l'uscita forzata e la terminazione di riserva.</summary>
    public static TimeSpan KillDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Avvia il conto alla rovescia. <paramref name="exit"/> è l'uscita forzata; se non porta via il processo entro 2 secondi segue <paramref name="kill"/>.</summary>
    public static ShutdownWatchdog Start(TimeSpan timeout, Action? exit = null, Action? kill = null, Action<string>? log = null, TimeSpan? killDelay = null)
    {
        var dog = new ShutdownWatchdog(timeout, exit ?? DefaultExit, kill ?? DefaultKill, log, killDelay ?? KillDelay);
        new Thread(dog.Run) { IsBackground = true, Name = "RdpManager-uscita" }.Start();
        return dog;
    }

    /// <summary>La chiusura è stata annullata: il cane da guardia non scatta più.</summary>
    public void Stop() => Interlocked.CompareExchange(ref _state, 1, 0);

    /// <summary>Vero se è scattato (per le prove).</summary>
    public bool Fired => Volatile.Read(ref _state) == 2;

    private void Run()
    {
        Thread.Sleep(_timeout);
        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
            return;
        try { _log?.Invoke($"Chiusura bloccata oltre {_timeout.TotalSeconds:0} secondi: uscita forzata"); } catch (Exception) { }
        // La terminazione di riserva si arma prima, su un thread suo: se _exit() si blocca (chiusura delle DLL, mstscax)
        // non deve impedirle di partire.
        var killDelay = _killDelay;
        new Thread(() =>
        {
            Thread.Sleep(killDelay);
            try { _kill(); } catch (Exception) { }
        }) { IsBackground = true, Name = "RdpManager-terminazione" }.Start();
        try { _exit(); } catch (Exception) { }
    }
}
