using Avalonia.Controls;
using RdpManager.Linux.ViewModels;

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
}
