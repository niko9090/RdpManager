using System.Runtime.CompilerServices;
using RdpManager.Core;
using RdpManager.Linux.Platform;
using RdpManager.Linux.Services;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Tests;

internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        SecretStore.Current = new FileKeySecretStore(Path.Combine(Path.GetTempPath(), "rdpm-linux-tests-" + Guid.NewGuid().ToString("N") + ".key"));
        SecretStore.Current.Protect([1], null); // crea subito la chiave, prima che partano i test in parallelo
        KdfSettings.ExportIterations = 1000;
        KdfSettings.PinIterations = 10_000;
        KdfSettings.SyncIterations = 1000;
    }
}

[CollectionDefinition("Globali", DisableParallelization = true)]
public sealed class GlobaliCollection;

/// <summary>Cartella temporanea con il file dei dati di una prova.</summary>
internal sealed class TempData : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-linux-" + Guid.NewGuid().ToString("N"));

    public TempData() => Directory.CreateDirectory(_dir);

    public string FilePath => Path.Combine(_dir, "connections.json");

    public ConnectionStore Store => new(FilePath);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool Confirm { get; set; } = true;
    public string? PromptAnswer { get; set; }
    public Action<ConnectionEditViewModel>? OnEdit { get; set; }
    public List<string> ConfirmMessages { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message)
    {
        ConfirmMessages.Add(message);
        return Task.FromResult(Confirm);
    }

    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult(PromptAnswer);

    public Task<bool> EditConnectionAsync(ConnectionEditViewModel editor)
    {
        OnEdit?.Invoke(editor);
        return Task.FromResult(editor.TryCommit());
    }

    public Task ShowProfilesAsync(ProfilesViewModel profiles) => Task.CompletedTask;

    public Task<bool> ShowSettingsAsync(SettingsViewModel settings) => Task.FromResult(false);
}

internal static class Seed
{
    public static void Write(ConnectionStore store, params RdpConnection[] connections)
    {
        var doc = new ConnectionsDocument();
        doc.Connections.AddRange(connections);
        store.Save(doc);
    }

    public static RdpConnection Conn(string name, string host, string group = "") =>
        new() { Name = name, Host = host, Group = group };

    public static MainWindowViewModel Main(TempData data, FakeDialogs? dialogs = null, ISessionHost? host = null) =>
        new(data.Store, host ?? new PlaceholderSessionHost(), dialogs ?? new FakeDialogs());
}
