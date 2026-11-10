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

    Task ShowWorkspacesAsync(WorkspacesViewModel workspaces);

    /// <summary>Finestra di sola lettura con un testo (pagina delle scorciatoie).</summary>
    Task ShowTextAsync(string title, string text);

    /// <summary>Chiede un testo segreto (password del server); null se annullato.</summary>
    Task<string?> AskSecretAsync(string title, string message);

    /// <summary>Avviso certificato: vero se l'utente si fida e vuole continuare.</summary>
    Task<bool> AskCertificateAsync(CertificatePrompt prompt);

    /// <summary>Avviso di una nuova versione: vero se l'utente vuole aprire la pagina della release.</summary>
    Task<bool> OfferUpdateAsync(ReleaseInfo release, string currentVersion);

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

    public virtual Task<bool> OfferUpdateAsync(ReleaseInfo release, string currentVersion)
    {
        Offers.Add(release);
        return Task.FromResult(OfferAnswer);
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
