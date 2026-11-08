namespace RdpManager.Linux.ViewModels;

/// <summary>Le finestre di dialogo che i viewmodel chiedono all'interfaccia (nelle prove sono sostituite da un finto).</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Chiede una riga di testo; null se annullato.</summary>
    Task<string?> PromptAsync(string title, string label, string initial);

    /// <summary>Mostra il modulo di modifica; vero se l'utente ha confermato (e <see cref="ConnectionEditViewModel.TryCommit"/> è riuscito).</summary>
    Task<bool> EditConnectionAsync(ConnectionEditViewModel editor);

    Task ShowProfilesAsync(ProfilesViewModel profiles);

    Task<bool> ShowSettingsAsync(SettingsViewModel settings);
}
