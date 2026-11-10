using RdpManager.Core;
using RdpManager.Linux.Services;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Tests;

/// <summary>Finto del servizio di dialoghi con risposte decise dalla prova (blocco A).</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    public bool Confirm { get; set; } = true;
    public List<string> ConfirmMessages { get; } = [];
    public List<string> Messages { get; } = [];
    public Func<object, bool>? OnShow { get; set; }
    public List<object> Shown { get; } = [];
    public IReadOnlyList<string> FilesToOpen { get; set; } = [];
    public string? SaveTo { get; set; }
    public Queue<string?> Secrets { get; } = new();
    public string? Choice { get; set; }
    public string? PromptAnswer { get; set; }
    public Func<SettingsViewModel, bool>? OnSettings { get; set; }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        ConfirmMessages.Add(message);
        return Task.FromResult(Confirm);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult(PromptAnswer);

    public Task<bool> EditConnectionAsync(ConnectionEditViewModel editor) => Task.FromResult(editor.TryCommit());

    public Task ShowProfilesAsync(ProfilesViewModel profiles) => Task.CompletedTask;

    public Task<bool> ShowSettingsAsync(SettingsViewModel settings) => Task.FromResult(OnSettings?.Invoke(settings) ?? false);

    public Task<bool> ShowAsync(object viewModel)
    {
        Shown.Add(viewModel);
        return Task.FromResult(OnShow?.Invoke(viewModel) ?? false);
    }

    public Task<string?> PromptSecretAsync(string title, string label) => Task.FromResult(Secrets.Count > 0 ? Secrets.Dequeue() : null);

    public Task<IReadOnlyList<string>> PickFilesToOpenAsync(string title, string filterName, IReadOnlyList<string> patterns, bool multiple) =>
        Task.FromResult(FilesToOpen);

    public Task<string?> PickFileToSaveAsync(string title, string suggestedName, string extension, string filterName) => Task.FromResult(SaveTo);

    public Task<string?> ChooseAsync(string title, string label, IReadOnlyList<string> options, string initial, bool editable) => Task.FromResult(Choice);
}

internal sealed class FakeShell : IShellService
{
    public List<string> Folders { get; } = [];
    public List<string> Urls { get; } = [];
    public bool Succeeds { get; set; } = true;

    public bool OpenFolder(string path)
    {
        Folders.Add(path);
        return Succeeds;
    }

    public bool OpenUrl(string url)
    {
        Urls.Add(url);
        return Succeeds;
    }
}

internal static class A
{
    public static MainWindowViewModel Vm(TempData data, ScriptedDialogs dialogs, FakeShell? shell = null, ISessionHost? host = null) =>
        new(data.Store, host ?? new PlaceholderSessionHost(), dialogs, shell ?? new FakeShell());

    /// <summary>Scrive il file dei dati con le connessioni (e opzionalmente le cartelle) e crea il modello.</summary>
    public static MainWindowViewModel Vm(TempData data, ScriptedDialogs dialogs, params RdpConnection[] connections)
    {
        Seed.Write(data.Store, connections);
        return Vm(data, dialogs);
    }

    public static TreeNodeViewModel Node(MainWindowViewModel vm, string name) =>
        Flatten(vm.Roots).First(n => n.Connection?.DisplayName == name || (n.IsFolder && n.FolderPath == name));

    public static IEnumerable<TreeNodeViewModel> Flatten(IEnumerable<TreeNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    public static string TempFile(TempData data, string name) => Path.Combine(Path.GetDirectoryName(data.FilePath)!, name);
}
