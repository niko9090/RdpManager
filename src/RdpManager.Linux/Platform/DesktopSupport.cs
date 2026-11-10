namespace RdpManager.Linux.Platform;

/// <summary>Cosa il desktop in uso permette (schermo touch, area di notifica). Le letture sono pure: il testo si passa da fuori.</summary>
public static class DesktopSupport
{
    /// <summary>Vero se l'elenco dei dispositivi di input di Linux (<c>/proc/bus/input/devices</c>) contiene uno schermo touch.</summary>
    public static bool HasTouchScreen(string? devicesText)
    {
        if (string.IsNullOrEmpty(devicesText))
            return false;
        foreach (var block in devicesText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var name = block.Split('\n').FirstOrDefault(l => l.StartsWith("N: Name=", StringComparison.Ordinal)) ?? "";
            if (name.Contains("touchscreen", StringComparison.OrdinalIgnoreCase)
                || (name.Contains("touch", StringComparison.OrdinalIgnoreCase) && !name.Contains("touchpad", StringComparison.OrdinalIgnoreCase)
                    && block.Contains("B: ABS=", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    public static bool DetectTouchScreen()
    {
        try
        {
            return OperatingSystem.IsLinux() && HasTouchScreen(File.ReadAllText("/proc/bus/input/devices"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Vero se l'output di <c>busctl --user list</c> mostra un servizio che ospita le icone dell'area di notifica
    /// (StatusNotifierWatcher). Senza, un'icona non si vedrebbe e «chiudi nell'area di notifica» farebbe sparire la finestra.
    /// </summary>
    public static bool HasTrayHost(string? busctlOutput) =>
        busctlOutput is not null && busctlOutput.Contains("StatusNotifierWatcher", StringComparison.Ordinal);
}
