namespace RdpManager.Tests;

/// <summary>
/// k-94: chi aggiorna dalla 1.13.0 è pilotato da un programma di aggiornamento vecchio, che non ferma niente prima
/// dell'installer: tutto ciò che libera i file in uso deve stare nell'installer. Inno Setup non gira nelle prove (e
/// l'installazione vera è vietata): si controlla il contratto dello script, che compila con ISCC (verificato a mano).
/// </summary>
public sealed class UpdateInstallerScriptTests
{
    private static string Iss()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "installer", "RdpManager.iss")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "installer", "RdpManager.iss"));
    }

    private static string Body(string iss, string header)
    {
        var start = iss.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, header);
        var end = iss.IndexOf("\nend;", start, StringComparison.Ordinal);
        return iss[start..end];
    }

    [Fact]
    public void Update_Installer_ClosesLeftoverProcessesOnlyFromTheInstalledFolder()
    {
        var iss = Iss();
        var close = Body(iss, "procedure CloseInstalledProcesses");
        // Filtra per percorso: il programma di aggiornamento è un RdpManager.exe della copia di riserva e non va fermato.
        Assert.Contains("$_.Path -like", close);
        Assert.Contains("AppDir", close);
        Assert.DoesNotContain("taskkill", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/IM", iss);
    }

    [Fact]
    public void Update_Installer_SilentSetupForcesClosureWhenTheExeStaysInUse()
    {
        var init = Body(Iss(), "function InitializeSetup");
        var firstWait = init.IndexOf("Waited < 60000", StringComparison.Ordinal);
        var kill = init.IndexOf("CloseInstalledProcesses(", StringComparison.Ordinal);
        var retryStop = init.IndexOf("StopVpnService;", kill, StringComparison.Ordinal);
        Assert.True(firstWait > 0 && kill > firstWait, "la chiusura forzata viene dopo l'attesa normale");
        Assert.True(retryStop > kill, "dopo la chiusura forzata si riprova a fermare il servizio");
        Assert.Contains("ancora in uso", init); // lo stato finale resta nel log dell'installer
    }
}
