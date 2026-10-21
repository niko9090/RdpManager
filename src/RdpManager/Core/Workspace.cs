namespace RdpManager.Core;

/// <summary>
/// Spazio di lavoro: un insieme di connessioni con il loro layout (schede aperte in quell'ordine, monitor, schermo
/// intero, vista affiancata), salvato con un nome e riaperto con un clic o con la sua scorciatoia.
/// </summary>
public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Connessioni da aprire, nell'ordine delle schede.</summary>
    public List<Guid> Connections { get; set; } = [];

    /// <summary>Scheda in primo piano dopo l'apertura (null = la prima).</summary>
    public Guid? Selected { get; set; }

    /// <summary>Monitor (Screen.DeviceName) su cui aprire la finestra o lo schermo intero; null = quello attuale.</summary>
    public string? Monitor { get; set; }

    public bool FullScreen { get; set; }

    /// <summary>Vista affiancata: connessioni a sinistra e a destra (entrambe null = niente vista affiancata).</summary>
    public Guid? SplitLeft { get; set; }
    public Guid? SplitRight { get; set; }
    public double SplitRatio { get; set; } = SplitLayout.DefaultRatio;

    /// <summary>Scorciatoia per riaprirlo (es. "Ctrl+Alt+Maiusc+1"); vuota = nessuna.</summary>
    public string Shortcut { get; set; } = "";

    public Workspace Clone()
    {
        var copy = (Workspace)MemberwiseClone();
        copy.Connections = [.. Connections];
        return copy;
    }

    internal void Repair()
    {
        Name ??= "";
        Shortcut ??= "";
        Connections ??= [];
        Connections = Connections.Where(id => id != Guid.Empty).Distinct().ToList();
        SplitRatio = SplitLayout.ClampRatio(SplitRatio);
        if (string.IsNullOrWhiteSpace(Monitor))
            Monitor = null;
        if (Shortcut.Length > 0 && !GlobalHotkey.TryParse(Shortcut, out _))
            Shortcut = "";
    }
}

/// <summary>Stato attuale della finestra da salvare in uno spazio di lavoro (lo raccoglie l'interfaccia).</summary>
public sealed record WorkspaceLayout(
    IReadOnlyList<Guid> OpenSessions, Guid? Selected, string? Monitor, bool FullScreen,
    Guid? SplitLeft, Guid? SplitRight, double SplitRatio);

/// <summary>Cosa fare per riaprire uno spazio di lavoro: solo connessioni che esistono ancora.</summary>
public sealed record WorkspacePlan(
    IReadOnlyList<RdpConnection> Open, RdpConnection? Selected, string? Monitor, bool FullScreen,
    RdpConnection? SplitLeft, RdpConnection? SplitRight, double SplitRatio, int Missing);

public sealed partial class ConnectionsDocument
{
    /// <summary>Spazi di lavoro salvati (dalla 1.4).</summary>
    public List<Workspace> Workspaces { get; set; } = [];

    public Workspace? FindWorkspace(Guid id) => Workspaces.FirstOrDefault(w => w.Id == id);

    /// <summary>
    /// Salva il layout attuale come spazio di lavoro <paramref name="name"/>: se ne esiste già uno con lo stesso nome
    /// lo aggiorna (tenendo la sua scorciatoia), altrimenti lo crea. Le connessioni che non esistono si scartano.
    /// </summary>
    public Workspace SaveWorkspace(string name, WorkspaceLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(layout);
        name = name.Trim();
        var ws = Workspaces.FirstOrDefault(w => w.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (ws is null)
        {
            ws = new Workspace { Name = name };
            Workspaces.Add(ws);
        }
        bool Known(Guid? id) => id is Guid g && Find(g) is not null;
        ws.Connections = layout.OpenSessions.Where(id => Known(id)).Distinct().ToList();
        ws.Selected = Known(layout.Selected) && ws.Connections.Contains(layout.Selected!.Value) ? layout.Selected : null;
        ws.Monitor = string.IsNullOrWhiteSpace(layout.Monitor) ? null : layout.Monitor;
        ws.FullScreen = layout.FullScreen;
        bool split = Known(layout.SplitLeft) && Known(layout.SplitRight) && layout.SplitLeft != layout.SplitRight
                     && ws.Connections.Contains(layout.SplitLeft!.Value) && ws.Connections.Contains(layout.SplitRight!.Value);
        ws.SplitLeft = split ? layout.SplitLeft : null;
        ws.SplitRight = split ? layout.SplitRight : null;
        ws.SplitRatio = SplitLayout.ClampRatio(layout.SplitRatio);
        return ws;
    }

    /// <summary>Piano per riaprire lo spazio: connessioni ancora esistenti nell'ordine salvato; le altre si contano.</summary>
    public WorkspacePlan PlanWorkspace(Workspace ws)
    {
        ArgumentNullException.ThrowIfNull(ws);
        var open = ws.Connections.Select(id => Find(id)).OfType<RdpConnection>().ToList();
        int missing = ws.Connections.Count - open.Count;
        RdpConnection? Pick(Guid? id) => id is Guid g ? open.FirstOrDefault(c => c.Id == g) : null;
        var left = Pick(ws.SplitLeft);
        var right = Pick(ws.SplitRight);
        if (left is null || right is null)
            left = right = null;
        return new WorkspacePlan(open, Pick(ws.Selected) ?? open.FirstOrDefault(), ws.Monitor, ws.FullScreen,
            left, right, SplitLayout.ClampRatio(ws.SplitRatio), missing);
    }

    /// <summary>Toglie una connessione eliminata dagli spazi di lavoro.</summary>
    public void RemoveFromWorkspaces(Guid connectionId)
    {
        foreach (var ws in Workspaces)
        {
            ws.Connections.Remove(connectionId);
            if (ws.Selected == connectionId)
                ws.Selected = null;
            if (ws.SplitLeft == connectionId || ws.SplitRight == connectionId)
                ws.SplitLeft = ws.SplitRight = null;
        }
    }
}
