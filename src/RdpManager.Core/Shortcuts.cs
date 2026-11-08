namespace RdpManager.Core;

/// <summary>Azioni raggiungibili da tastiera anche col fuoco nella sessione remota.</summary>
public enum ShortcutAction
{
    NextSession,
    PreviousSession,
    Session1, Session2, Session3, Session4, Session5, Session6, Session7, Session8, Session9,
    ToggleFullScreen,
    ToggleSidebar,
    // Dalla 1.2
    Overview,
    QuickSearch,
    ToggleSplitView,
    // Dalla 1.4
    Lock,
}

/// <summary>Passaggio fra le sessioni aperte, in ordine di scheda, saltando quelle non connesse.</summary>
public static class SessionCycler
{
    /// <summary>
    /// Indice della prossima sessione connessa dopo (o prima di) <paramref name="current"/>, ricominciando dall'altro capo.
    /// Se nessuna è connessa restituisce -1; se è connessa solo quella corrente restituisce quella.
    /// <paramref name="current"/> fuori dall'elenco (es. -1) parte dall'inizio o dalla fine.
    /// </summary>
    public static int Next(IReadOnlyList<bool> connected, int current, bool forward = true)
    {
        ArgumentNullException.ThrowIfNull(connected);
        int n = connected.Count;
        if (n == 0)
            return -1;
        int start = current >= 0 && current < n ? current : (forward ? -1 : n);
        for (int step = 1; step <= n; step++)
        {
            int i = ((start + (forward ? step : -step)) % n + n) % n;
            if (connected[i])
                return i;
        }
        return -1;
    }

    public static int Previous(IReadOnlyList<bool> connected, int current) => Next(connected, current, forward: false);

    /// <summary>Indice della <paramref name="number"/>-esima sessione connessa (da 1), oppure -1.</summary>
    public static int Nth(IReadOnlyList<bool> connected, int number)
    {
        ArgumentNullException.ThrowIfNull(connected);
        if (number < 1)
            return -1;
        for (int i = 0, seen = 0; i < connected.Count; i++)
            if (connected[i] && ++seen == number)
                return i;
        return -1;
    }

    /// <summary>Indice dell'azione di scorciatoia, oppure null se l'azione non riguarda il cambio di sessione o non c'è dove andare.</summary>
    public static int? Target(ShortcutAction action, IReadOnlyList<bool> connected, int current)
    {
        int index = action switch
        {
            ShortcutAction.NextSession => Next(connected, current, forward: true),
            ShortcutAction.PreviousSession => Next(connected, current, forward: false),
            _ when action is >= ShortcutAction.Session1 and <= ShortcutAction.Session9 => Nth(connected, action - ShortcutAction.Session1 + 1),
            _ => -1,
        };
        return index < 0 ? null : index;
    }
}
