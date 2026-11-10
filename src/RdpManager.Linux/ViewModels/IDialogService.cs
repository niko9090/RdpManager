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

    // --- Aggiunte del blocco A. Hanno un'implementazione di partenza (annulla) così i finti delle prove non devono implementarle tutte. ---

    /// <summary>
    /// Mostra la finestra del modello indicato (cartella, ricerca rapida, cronologia, testi rapidi, importazioni, PIN, rete…).
    /// Vero se l'utente ha confermato; i modelli di sola consultazione restituiscono vero alla chiusura.
    /// </summary>
    Task<bool> ShowAsync(object viewModel) => Task.FromResult(false);

    /// <summary>Chiede una riga di testo segreta (password del file); null se annullato.</summary>
    Task<string?> PromptSecretAsync(string title, string label) => Task.FromResult<string?>(null);

    /// <summary>Sceglie uno o più file da aprire (<paramref name="patterns"/> come «*.rdp»); elenco vuoto se annullato.</summary>
    Task<IReadOnlyList<string>> PickFilesToOpenAsync(string title, string filterName, IReadOnlyList<string> patterns, bool multiple) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Sceglie dove salvare un file; null se annullato.</summary>
    Task<string?> PickFileToSaveAsync(string title, string suggestedName, string extension, string filterName) =>
        Task.FromResult<string?>(null);

    /// <summary>Sceglie una voce da un elenco (anche scrivendone una nuova se <paramref name="editable"/>); null se annullato.</summary>
    Task<string?> ChooseAsync(string title, string label, IReadOnlyList<string> options, string initial, bool editable) =>
        Task.FromResult<string?>(null);
}
