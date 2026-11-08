namespace RdpManager.Core;

/// <summary>Le scorciatoie da tastiera di <see cref="AppSettings"/>: legate ai tasti di Windows Forms, quindi fuori dal nucleo.</summary>
public static class AppSettingsShortcuts
{
    public static ShortcutMap GetShortcuts(this AppSettings settings) => ShortcutMap.FromSettings(settings.Shortcuts);

    public static void SetShortcuts(this AppSettings settings, ShortcutMap map) => settings.Shortcuts = map.ToSettings();
}