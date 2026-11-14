using System.Collections.ObjectModel;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels;

/// <summary>Una riga dell'albero: una cartella (con figli) oppure una connessione.</summary>
public sealed class TreeNodeViewModel : ViewModelBase
{
    private bool _isExpanded;
    private ReachabilityStatus _reachability;

    private readonly string? _section;

    private TreeNodeViewModel(string title, string subtitle, string folderPath, RdpConnection? connection, string? section = null, bool isSection = false)
    {
        Title = title;
        Subtitle = subtitle;
        FolderPath = folderPath;
        Connection = connection;
        _section = section;
        IsSection = isSection;
    }

    public static TreeNodeViewModel ForFolder(string path) =>
        new(path[(path.LastIndexOf('/') + 1)..], "", path, null);

    /// <summary>Sezione rapida in cima all'elenco («Preferiti», «Recenti»), come nel programma Windows.</summary>
    public static TreeNodeViewModel ForSection(string name) => new(name, "", "", null, name, isSection: true);

    /// <summary>
    /// Una connessione. Come in Windows il titolo non porta la stella (i preferiti stanno nella sezione «Preferiti»);
    /// <paramref name="section"/> è la sezione rapida che la mostra (stessa connessione, riga diversa).
    /// </summary>
    public static TreeNodeViewModel ForConnection(RdpConnection c, string? section = null)
    {
        var title = c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : "");
        var target = c.Port == RdpConnection.DefaultPort ? c.Host : $"{c.Host}:{c.Port}";
        return new(title, target, c.Group, c, section);
    }

    public string Title { get; }

    /// <summary>Intestazione di una sezione rapida (Preferiti, Recenti): in grassetto, senza connessione né cartella.</summary>
    public bool IsSection { get; }

    public bool IsConnection => Connection is not null;

    /// <summary>Colore dello schermo nell'icona del PC: l'etichetta della connessione oppure l'azzurro di Windows.</summary>
    public string ScreenColor => HasLabelColor ? LabelColor : "#4688DC";

    /// <summary>Il pallino di stato nell'icona si vede (la raggiungibilità è nota).</summary>
    public bool HasStatusColor => StatusColor.Length > 0;

    /// <summary>Suggerimento della riga, come in Windows: «indirizzo — stato».</summary>
    public string ToolTipText => Connection is null ? "" : $"{(Connection is { } c ? RdpFile.FormatAddress(c.Host, c.Port) : Subtitle)} — {StatusText}";

    /// <summary>Indirizzo mostrato sotto il nome (solo connessioni).</summary>
    public string Subtitle { get; }

    /// <summary>Percorso della cartella (per una connessione, quella che la contiene).</summary>
    public string FolderPath { get; }

    public RdpConnection? Connection { get; }

    public bool IsFolder => Connection is null && !IsSection;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>Chiave stabile per ritrovare la riga dopo una ricostruzione dell'albero.</summary>
    public string Key => IsSection ? "s:" + _section
        : Connection is { } c ? (_section is null ? "c:" : "q:" + _section + ":") + c.Id
        : "f:" + FolderPath;

    /// <summary>Colore dell'etichetta («#RRGGBB»); vuoto se la connessione non ne ha.</summary>
    public string LabelColor => Connection?.Color is { } c ? RdpConnection.NormalizeColor(c) ?? "" : "";

    public bool HasLabelColor => LabelColor.Length > 0;

    /// <summary>Esito dell'ultima verifica di raggiungibilità (#13).</summary>
    public ReachabilityStatus Reachability
    {
        get => _reachability;
        set
        {
            if (!Set(ref _reachability, value))
                return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(HasStatusColor));
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    /// <summary>«Raggiungibile», «Non raggiungibile» o «Non verificata» (solo connessioni).</summary>
    public string StatusText => Connection is null ? "" : _reachability switch
    {
        ReachabilityStatus.Reachable => "Raggiungibile",
        ReachabilityStatus.Unreachable => "Non raggiungibile",
        _ => "Non verificata",
    };

    /// <summary>Colore del pallino di stato: verde se raggiungibile, grigio se no, vuoto se non verificata.</summary>
    public string StatusColor => _reachability switch
    {
        ReachabilityStatus.Reachable => "#2EA043",
        ReachabilityStatus.Unreachable => "#969696",
        _ => "",
    };
}
