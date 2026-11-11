using Avalonia.Controls;

namespace RdpManager.Linux.Dialogs;

/// <summary>
/// Tiene il conto dei dialoghi modali aperti: il blocco con il PIN (#28) li chiude, perché altrimenti da bloccata
/// resterebbero visibili e utilizzabili (impostazioni, modifica di una connessione…).
/// </summary>
public static class DialogTracker
{
    private static readonly List<Window> Open = [];

    /// <summary>Quanti dialoghi sono aperti ora.</summary>
    public static int Count => Open.Count;

    /// <summary>Come <c>ShowDialog</c>, ma ricorda la finestra finché è aperta.</summary>
    public static async Task<T> ShowDialogTracked<T>(this Window window, Window owner)
    {
        Open.Add(window);
        WindowBehaviors.TrackActivity(window);
        try
        {
            return await window.ShowDialog<T>(owner);
        }
        finally
        {
            Open.Remove(window);
        }
    }

    /// <inheritdoc cref="ShowDialogTracked{T}"/>
    public static async Task ShowDialogTracked(this Window window, Window owner)
    {
        Open.Add(window);
        WindowBehaviors.TrackActivity(window);
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            Open.Remove(window);
        }
    }

    /// <summary>Chiude tutti i dialoghi aperti (i più recenti per primi).</summary>
    public static void CloseAll()
    {
        foreach (var window in Open.AsEnumerable().Reverse().ToList())
        {
            try { window.Close(); }
            catch (InvalidOperationException) { }
        }
    }
}
