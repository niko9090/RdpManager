using RdpManager.Core;
using RdpManager.Linux.Features;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «Area di notifica» (blocco B): icona nel vassoio, chiusura nel vassoio, scorciatoia globale di richiamo.</summary>
public sealed class VassoioPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private bool _showTray, _closeToTray;
    private string _hotkey;

    public VassoioPageViewModel(AppSettings settings)
    {
        _settings = settings;
        _showTray = settings.ShowTrayIcon;
        _closeToTray = settings.CloseToTray;
        _hotkey = settings.GlobalHotkey;
    }

    public override string Title => "Area di notifica";

    public bool ShowTrayIcon
    {
        get => _showTray;
        set
        {
            Set(ref _showTray, value);
            if (!value)
                CloseToTray = false;
            OnPropertyChanged(nameof(CloseToTrayEnabled));
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            Set(ref _closeToTray, value && ShowTrayIcon);
            // Sempre: se la casella è stata spuntata ma il valore reale resta spento, la vista deve tornare allineata.
            OnPropertyChanged(nameof(CloseToTray));
            OnPropertyChanged(nameof(CloseToTrayEnabled));
        }
    }

    public bool CloseToTrayEnabled => ShowTrayIcon;

    /// <summary>Scorciatoia globale, per esempio «Ctrl+Alt+Maiusc+R»; vuota = disattivata.</summary>
    public string GlobalHotkey
    {
        get => _hotkey;
        set
        {
            if (Set(ref _hotkey, value))
                OnPropertyChanged(nameof(HotkeyError));
        }
    }

    public bool HotkeyAvailable => DisplayServer.CanGlobalHotkey(DisplayServer.Current);

    /// <summary>Vuoto se la scorciatoia va bene; altrimenti il motivo.</summary>
    public string HotkeyError => IsValidHotkey(_hotkey) ? "" : "La scorciatoia non è valida: serve Ctrl o Alt più un tasto (per esempio Ctrl+Alt+Maiusc+R).";

    public string HotkeyNote => DisplayServer.GlobalHotkeyNote(DisplayServer.Current);

    public string TrayNote => DisplayServer.TrayNote(DisplayServer.Current);

    internal static bool IsValidHotkey(string? text) =>
        string.IsNullOrWhiteSpace(text) || (KeyCombo.TryParse(text, out var combo) && combo.HasCommandModifier);

    public override void Apply()
    {
        _settings.ShowTrayIcon = ShowTrayIcon;
        _settings.CloseToTray = CloseToTray;
        if (IsValidHotkey(_hotkey))
            _settings.GlobalHotkey = KeyCombo.TryParse(_hotkey, out var combo) ? combo.ToString() : "";
    }
}
