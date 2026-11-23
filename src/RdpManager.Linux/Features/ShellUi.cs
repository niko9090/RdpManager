using RdpManager.Core;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Features;

/// <summary>
/// Le finestre e le operazioni di sistema che il blocco B chiede all'interfaccia (nelle prove è sostituita da <see cref="NullShellUi"/>).
/// Le finestre del blocco A passano da <c>IDialogService</c>.
/// </summary>
public interface IShellUi
{
    Task ShowAccountAsync(AccountViewModel account);

    Task ShowVpnAsync(VpnViewModel vpn);

    /// <summary>Plancia VPN: finestra con i PC della VPN; se ne sceglie uno, <see cref="VpnDashboardViewModel.Chosen"/> lo indica alla chiusura.</summary>
    Task ShowVpnDashboardAsync(VpnDashboardViewModel dashboard);

    Task ShowWorkspacesAsync(WorkspacesViewModel workspaces);

    /// <summary>Finestra di sola lettura con un testo (pagina delle scorciatoie).</summary>
    Task ShowTextAsync(string title, string text);

    /// <summary>Chiede un testo segreto (password del server); null se annullato.</summary>
    Task<string?> AskSecretAsync(string title, string message);

    /// <summary>Avviso certificato: vero se l'utente si fida e vuole continuare.</summary>
    Task<bool> AskCertificateAsync(CertificatePrompt prompt);

    /// <summary>
    /// Avviso di una nuova versione: vero se l'utente accetta. Con <paramref name="canInstall"/> si offre l'installazione
    /// automatica («Installa ora»); senza, solo l'apertura della pagina della release.
    /// </summary>
    Task<bool> OfferUpdateAsync(ReleaseInfo release, string currentVersion, bool canInstall = false);

    /// <summary>Finestra di avanzamento dello scaricamento; si chiude con Dispose. «Annulla» scatta <see cref="IUpdateProgressView.Cancelled"/>.</summary>
    IUpdateProgressView ShowUpdateProgress(ReleaseInfo release);

    /// <summary>Messaggio con «OK» sull'esito dell'aggiornamento (riuscito o non riuscito); finisce quando l'utente preme OK.</summary>
    Task ShowUpdateMessageAsync(string title, string message);

    /// <summary>Selettore di file; null se annullato.</summary>
    Task<string?> PickFileAsync(string title, string filterName, params string[] patterns);

    Task<string?> GetClipboardTextAsync();

    Task SetClipboardTextAsync(string text);

    /// <summary>Apre un indirizzo web nel browser (xdg-open).</summary>
    void OpenUrl(string url);

    /// <summary>Esegue <paramref name="action"/> sul thread dell'interfaccia e restituisce il risultato.</summary>
    T Invoke<T>(Func<T> action);

    /// <summary>Panoramica delle sessioni: restituisce la scheda scelta, o null.</summary>
    Task<SessionTabViewModel?> ShowOverviewAsync(IReadOnlyList<SessionTabViewModel> tabs, SessionTabViewModel? selected);
}

/// <summary>Avanzamento dello scaricamento di un aggiornamento (come UpdateProgressForm su Windows).</summary>
public interface IUpdateProgressView : IDisposable
{
    /// <summary>Scatta se l'utente preme «Annulla».</summary>
    CancellationToken Cancelled { get; }

    /// <summary>Avanzamento da 0 a 1.</summary>
    void Report(double fraction);

    void SetText(string text);

    /// <summary>Barra senza percentuale (installazione in corso, durata non nota).</summary>
    void SetIndeterminate(bool on);
}

/// <summary>Avanzamento senza finestra (prove).</summary>
public sealed class NullUpdateProgress : IUpdateProgressView
{
    public CancellationToken Cancelled { get; set; }
    public double Last { get; private set; }
    public string Text { get; private set; } = "";
    public List<string> Texts { get; } = [];
    public bool Indeterminate { get; private set; }
    public bool Disposed { get; private set; }
    public void Report(double fraction) => Last = fraction;
    public void SetText(string text)
    {
        Text = text;
        Texts.Add(text);
    }
    public void SetIndeterminate(bool on) => Indeterminate = on;
    public void Dispose() => Disposed = true;
}

/// <summary>Cosa mostrare nell'avviso sul certificato del server.</summary>
public sealed record CertificatePrompt(string Host, string Detail);

/// <summary>Interfaccia nulla: niente finestre, risposte predefinite (per le prove e per il funzionamento senza schermo).</summary>
public class NullShellUi : IShellUi
{
    public bool CertificateAnswer { get; set; }
    public bool OfferAnswer { get; set; }
    public string? SecretAnswer { get; set; }
    public string? FileAnswer { get; set; }
    public string? Clipboard { get; set; }
    public List<string> OpenedUrls { get; } = [];
    public List<CertificatePrompt> CertificatePrompts { get; } = [];
    public List<string> ShownTexts { get; } = [];
    public List<ReleaseInfo> Offers { get; } = [];

    public virtual Task ShowAccountAsync(AccountViewModel account) => Task.CompletedTask;
    public virtual Task ShowVpnAsync(VpnViewModel vpn) => Task.CompletedTask;
    public virtual Task ShowVpnDashboardAsync(VpnDashboardViewModel dashboard) => Task.CompletedTask;
    public virtual Task ShowWorkspacesAsync(WorkspacesViewModel workspaces) => Task.CompletedTask;

    public virtual Task ShowTextAsync(string title, string text)
    {
        ShownTexts.Add(text);
        return Task.CompletedTask;
    }

    public virtual Task<string?> AskSecretAsync(string title, string message) => Task.FromResult(SecretAnswer);

    public virtual Task<bool> AskCertificateAsync(CertificatePrompt prompt)
    {
        CertificatePrompts.Add(prompt);
        return Task.FromResult(CertificateAnswer);
    }

    public List<bool> OffersCanInstall { get; } = [];
    public List<NullUpdateProgress> Progresses { get; } = [];

    public virtual Task<bool> OfferUpdateAsync(ReleaseInfo release, string currentVersion, bool canInstall = false)
    {
        Offers.Add(release);
        OffersCanInstall.Add(canInstall);
        return Task.FromResult(OfferAnswer);
    }

    public virtual IUpdateProgressView ShowUpdateProgress(ReleaseInfo release)
    {
        var p = new NullUpdateProgress();
        Progresses.Add(p);
        return p;
    }

    public List<(string Title, string Message)> UpdateMessages { get; } = [];

    public virtual Task ShowUpdateMessageAsync(string title, string message)
    {
        UpdateMessages.Add((title, message));
        return Task.CompletedTask;
    }

    public virtual Task<string?> PickFileAsync(string title, string filterName, params string[] patterns) => Task.FromResult(FileAnswer);
    public virtual Task<string?> GetClipboardTextAsync() => Task.FromResult(Clipboard);

    public virtual Task SetClipboardTextAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }

    public virtual void OpenUrl(string url) => OpenedUrls.Add(url);
    public virtual T Invoke<T>(Func<T> action) => action();
    public virtual Task<SessionTabViewModel?> ShowOverviewAsync(IReadOnlyList<SessionTabViewModel> tabs, SessionTabViewModel? selected) =>
        Task.FromResult<SessionTabViewModel?>(null);
}
