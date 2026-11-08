using System.Collections.ObjectModel;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels;

/// <summary>Una riga dell'albero: una cartella (con figli) oppure una connessione.</summary>
public sealed class TreeNodeViewModel : ViewModelBase
{
    private bool _isExpanded;

    private TreeNodeViewModel(string title, string subtitle, string folderPath, RdpConnection? connection)
    {
        Title = title;
        Subtitle = subtitle;
        FolderPath = folderPath;
        Connection = connection;
    }

    public static TreeNodeViewModel ForFolder(string path) =>
        new(path[(path.LastIndexOf('/') + 1)..], "", path, null);

    public static TreeNodeViewModel ForConnection(RdpConnection c)
    {
        var title = (c.IsFavorite ? "★ " : "") + c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : "");
        var target = c.Port == RdpConnection.DefaultPort ? c.Host : $"{c.Host}:{c.Port}";
        return new(title, target, c.Group, c);
    }

    public string Title { get; }

    /// <summary>Indirizzo mostrato sotto il nome (solo connessioni).</summary>
    public string Subtitle { get; }

    /// <summary>Percorso della cartella (per una connessione, quella che la contiene).</summary>
    public string FolderPath { get; }

    public RdpConnection? Connection { get; }

    public bool IsFolder => Connection is null;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>Chiave stabile per ritrovare la riga dopo una ricostruzione dell'albero.</summary>
    public string Key => Connection is { } c ? "c:" + c.Id : "f:" + FolderPath;
}
