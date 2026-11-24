using System.Globalization;

namespace RdpManager.Core;

/// <summary>
/// Esito di <see cref="MonitorSelection.Resolve"/>.
/// </summary>
/// <param name="Screens">Schermi da usare, nell'ordine dell'elenco rilevato (mai vuoto se l'elenco rilevato non lo era).</param>
/// <param name="Indexes">Posizioni di <paramref name="Screens"/> nell'elenco rilevato (gli indici di "selectedmonitors").</param>
/// <param name="UseMultimon">La sessione deve essere multi-schermo (più di uno schermo da coprire).</param>
/// <param name="FellBack">La scelta non era applicabile del tutto (schermo scelto non più collegato): si è usato un ripiego.</param>
/// <param name="MissingDevices">Descrizione degli schermi scelti non trovati (nome dispositivo o "#indice"), per avvisare l'utente.</param>
public sealed record MonitorResolution(IReadOnlyList<ScreenInfo> Screens, IReadOnlyList<int> Indexes, bool UseMultimon,
    bool FellBack, IReadOnlyList<string> MissingDevices);

/// <summary>Quali schermi usare per una connessione, dato l'elenco rilevato; uguale per Windows e Linux.</summary>
public static class MonitorSelection
{
    /// <summary>
    /// Single: lo schermo principale (o il primo). All: tutti. Selected: quelli scelti ancora collegati — cercati per nome
    /// di dispositivo se la connessione li ha salvati, altrimenti per indice; se alcuni mancano si usano gli altri
    /// (FellBack = true); se non ne resta nessuno si ripiega su tutti gli schermi presenti (FellBack = true).
    /// Con elenco vuoto il risultato è vuoto e non multi-schermo.
    /// </summary>
    public static MonitorResolution Resolve(IReadOnlyList<ScreenInfo> detected, RdpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(connection);
        if (detected.Count == 0)
            return new MonitorResolution([], [], false, false, []);

        switch (connection.MonitorMode)
        {
            case MonitorMode.All:
                return Of(detected, Enumerable.Range(0, detected.Count), false, []);
            case MonitorMode.Selected when connection.SelectedMonitors.Length > 0:
                var found = new SortedSet<int>();
                var missing = new List<string>();
                var devices = connection.SelectedMonitorDevices;
                for (int k = 0; k < connection.SelectedMonitors.Length; k++)
                {
                    int index = connection.SelectedMonitors[k];
                    string? device = k < devices.Length && !string.IsNullOrWhiteSpace(devices[k]) ? devices[k] : null;
                    int at = device is not null
                        ? IndexOfDevice(detected, device)
                        : index >= 0 && index < detected.Count ? index : -1;
                    if (at >= 0)
                        found.Add(at);
                    else
                        missing.Add(device ?? "#" + index.ToString(CultureInfo.InvariantCulture));
                }
                if (found.Count == 0)
                    return Of(detected, Enumerable.Range(0, detected.Count), true, missing);
                return Of(detected, found, missing.Count > 0, missing);
            default: // Single, o Selected con elenco vuoto
                var main = MonitorLayout.ChooseScreen(detected, null)!;
                return Of(detected, [detected.ToList().IndexOf(main)], false, []);
        }
    }

    /// <summary>
    /// Argomenti FreeRDP: nessuno se la sessione non è multi-schermo; "/multimon" se copre tutti gli
    /// <paramref name="detectedCount"/> schermi; "/multimon /monitors:0,1" per un sottoinsieme.
    /// (Gli indici di FreeRDP possono differire da quelli dell'elenco rilevato: chi lo lancia li verifichi con /list:monitor.)
    /// </summary>
    public static IReadOnlyList<string> FreeRdpArguments(MonitorResolution resolution, int detectedCount)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.UseMultimon)
            return [];
        return resolution.Indexes.Count >= detectedCount
            ? ["/multimon"]
            : ["/multimon", "/monitors:" + string.Join(",", resolution.Indexes)];
    }

    /// <summary>Elenco di indici "0,1,3" (come "selectedmonitors"): ignora ciò che non è un numero ≥ 0; ordinato, senza doppioni.</summary>
    public static int[] ParseIndexes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n >= 0).Distinct().Order().ToArray();
    }

    private static int IndexOfDevice(IReadOnlyList<ScreenInfo> screens, string device)
    {
        for (int i = 0; i < screens.Count; i++)
            if (screens[i].DeviceName.Equals(device, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static MonitorResolution Of(IReadOnlyList<ScreenInfo> detected, IEnumerable<int> indexes, bool fellBack,
        IReadOnlyList<string> missing)
    {
        var list = indexes.ToList();
        return new MonitorResolution(list.Select(i => detected[i]).ToList(), list, list.Count > 1, fellBack, missing);
    }
}
