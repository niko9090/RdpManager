using System.Windows.Forms;

namespace RdpManager.Tests;

/// <summary>
/// Thread STA delle prove di interfaccia con un limite di tempo: nessuna prova può restare appesa
/// all'infinito (un testhost orfano terrebbe poi bloccata la DLL e il cancello di unione).
/// </summary>
internal static class StaTestRunner
{
    public const int LimitSeconds = 60;

    /// <summary>Crea il thread STA (sfondo, non avviato). Sul thread un timer chiude il ciclo messaggi allo scadere.</summary>
    public static Thread Create(Action body)
    {
        var thread = new Thread(() =>
        {
            // Un'eccezione dell'interfaccia deve far fallire la prova, non aprire il dialogo di .NET e bloccarla.
            // Va impostato prima di creare qualunque controllo (compreso il timer qui sotto).
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            using var watchdog = new System.Windows.Forms.Timer { Interval = (LimitSeconds - 5) * 1000 };
            watchdog.Tick += (_, _) => Application.ExitThread();
            watchdog.Start();
            body();
        })
        {
            IsBackground = true,
            Name = "StaTestRunner",
        };
        thread.SetApartmentState(ApartmentState.STA);
        return thread;
    }

    /// <summary>Avvia il thread e attende al massimo <see cref="LimitSeconds"/> secondi; se scade la prova fallisce.</summary>
    public static void Run(Thread thread, int seconds = LimitSeconds)
    {
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(seconds)))
            throw new Xunit.Sdk.XunitException(
                $"La prova UI non è terminata entro {seconds} s (thread STA abbandonato: finestra modale o ciclo messaggi appeso).");
    }

    /// <summary>Esegue il corpo su un thread STA con il limite di tempo.</summary>
    public static void Run(Action body, int seconds = LimitSeconds) => Run(Create(body), seconds);
}
