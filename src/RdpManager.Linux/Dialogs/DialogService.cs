using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RdpManager.Linux.Dialogs.Tools;
using RdpManager.Linux.ViewModels;
using RdpManager.Linux.ViewModels.Tools;

namespace RdpManager.Linux.Dialogs;

/// <summary>Le finestre di dialogo vere, aperte sopra la finestra principale.</summary>
public sealed class DialogService : IDialogService
{
    public Window? Owner { get; set; }

    private Window RequireOwner() => Owner ?? throw new InvalidOperationException("Finestra principale non ancora creata.");

    public Task<bool> ConfirmAsync(string title, string message) => TextDialog.ConfirmAsync(RequireOwner(), title, message);

    public Task ShowMessageAsync(string title, string message) => TextDialog.MessageAsync(RequireOwner(), title, message);

    public Task<string?> PromptAsync(string title, string label, string initial) =>
        TextDialog.PromptAsync(RequireOwner(), title, label, initial);

    public Task<bool> EditConnectionAsync(ConnectionEditViewModel editor) =>
        new ConnectionEditWindow { DataContext = editor }.ShowDialog<bool>(RequireOwner());

    public Task ShowProfilesAsync(ProfilesViewModel profiles) =>
        new ProfilesWindow { DataContext = profiles }.ShowDialog(RequireOwner());

    public Task<bool> ShowSettingsAsync(SettingsViewModel settings) =>
        new SettingsWindow { DataContext = settings }.ShowDialog<bool>(RequireOwner());

    public Task<bool> ShowAsync(object viewModel)
    {
        Window window = viewModel switch
        {
            FolderSettingsViewModel vm => new FolderSettingsWindow(vm),
            QuickSearchViewModel vm => new QuickSearchWindow(vm),
            HistoryViewModel vm => new HistoryWindow(vm),
            QuickTextsViewModel vm => new QuickTextsWindow(vm),
            RdpmExportViewModel vm => new RdpmExportWindow(vm),
            RdpmImportViewModel vm => new RdpmImportWindow(vm),
            CsvImportViewModel vm => new CsvImportWindow(vm),
            NetworkScanViewModel vm => new NetworkScanWindow(vm),
            PinSetViewModel vm => new PinSetWindow(vm),
            _ => throw new NotSupportedException($"Nessuna finestra per {viewModel.GetType().Name}."),
        };
        return window.ShowDialog<bool>(RequireOwner());
    }

    public Task<string?> PromptSecretAsync(string title, string label) =>
        TextDialog.PromptSecretAsync(RequireOwner(), title, label);

    public Task<string?> ChooseAsync(string title, string label, IReadOnlyList<string> options, string initial, bool editable) =>
        TextDialog.ChooseAsync(RequireOwner(), title, label, options, initial, editable);

    public async Task<IReadOnlyList<string>> PickFilesToOpenAsync(string title, string filterName, IReadOnlyList<string> patterns, bool multiple)
    {
        var files = await RequireOwner().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFileToSaveAsync(string title, string suggestedName, string extension, string filterName)
    {
        var file = await RequireOwner().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = [$"*.{extension}"] }],
        });
        return file?.TryGetLocalPath();
    }
}
