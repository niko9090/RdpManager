using Avalonia.Controls;

namespace RdpManager.Linux.Dialogs;

/// <summary>
/// Tiene il conto dei dialoghi modali aperti: il blocco con il PIN (#28) li chiude, perché altrimenti da bloccata
/// resterebbero visibili e utilizzabili (impostazioni, modifica di una connessione…).
/// </summary>
public static class DialogTracker
{
    private static readonly List<Window> Open = [];

    /// <summary>Per le prove: se impostato, al posto di <c>ShowDialog</c> (modale, bloccante) si chiama questa funzione con (dialogo, proprietaria).</summary>
    internal static Func<Window, Window, Task>? PresenterForTests { get; set; }

    /// <summary>Quanti dialoghi sono aperti ora.</summary>
    public static int Count => Open.Count;

    /// <summary>Come <c>ShowDialog</c>, ma ricorda la finestra finché è aperta.</summary>
    public static async Task<T> ShowDialogTracked<T>(this Window window, Window owner)
    {
        Open.Add(window);
        WindowBehaviors.TrackActivity(window);
        DialogFit.Prepare(window, owner);
        try
        {
            if (PresenterForTests is { } present)
            {
                await present(window, owner);
                return default!;
            }
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
        DialogFit.Prepare(window, owner);
        try
        {
            if (PresenterForTests is { } present)
                await present(window, owner);
            else
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
