using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Cosa fa un gesto a più dita: una riga della pagina «Tocco».</summary>
public sealed class GestureSettingItem : ViewModelBase
{
    private ChoiceItem<TouchActionKind?> _action;

    public GestureSettingItem(TouchGesture gesture, TouchActionKind? action)
    {
        Gesture = gesture;
        _action = ToccoPageViewModel.ActionChoices.First(c => c.Value == action);
    }

    public TouchGesture Gesture { get; }

    public string Name => TouchGestureMap.Describe(Gesture);

    public IReadOnlyList<ChoiceItem<TouchActionKind?>> Choices => ToccoPageViewModel.ActionChoices;

    public ChoiceItem<TouchActionKind?> Action { get => _action; set { if (value is not null) Set(ref _action, value); } }
}

/// <summary>Pagina «Tocco» (blocco B): modalità tocco, dito nella sessione, gesti a più dita.</summary>
public sealed class ToccoPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private ChoiceItem<TouchMode> _mode;
    private ChoiceItem<SessionTouchMode> _session;

    public ToccoPageViewModel(AppSettings settings)
    {
        _settings = settings;
        _mode = Choices.Pick(ModeChoices, settings.TouchMode);
        _session = Choices.Pick(SessionChoices, settings.SessionTouch);
        var map = settings.GestureMap;
        Gestures = Enum.GetValues<TouchGesture>().Select(g => new GestureSettingItem(g, map.ActionFor(g))).ToList();
    }

    public override string Title => "Tocco";

    public static IReadOnlyList<ChoiceItem<TouchMode>> ModeChoices { get; } =
    [
        new(TouchMode.Auto, "Automatica (se c'è uno schermo touch)"),
        new(TouchMode.On, "Sempre attiva"),
        new(TouchMode.Off, "Disattivata"),
    ];

    public static IReadOnlyList<ChoiceItem<SessionTouchMode>> SessionChoices { get; } =
        Enum.GetValues<SessionTouchMode>().Select(m => new ChoiceItem<SessionTouchMode>(m, SessionTouchModes.Describe(m))).ToList();

    public static IReadOnlyList<ChoiceItem<TouchActionKind?>> ActionChoices { get; } =
        new List<ChoiceItem<TouchActionKind?>> { new(null, TouchGestureMap.Describe((TouchActionKind?)null)) }
            .Concat(TouchGestureMap.Choices.Select(k => new ChoiceItem<TouchActionKind?>(k, TouchGestureMap.Describe(k))))
            .ToList();

    public ChoiceItem<TouchMode> Mode { get => _mode; set { if (value is not null) Set(ref _mode, value); } }

    public ChoiceItem<SessionTouchMode> SessionMode { get => _session; set { if (value is not null) Set(ref _session, value); } }

    public IReadOnlyList<GestureSettingItem> Gestures { get; }

    public string Note => "La modalità tocco ingrandisce righe, schede, menu e pulsanti. I gesti a più dita valgono nella finestra dell'applicazione; "
        + "dentro una sessione incorporata (finestra di FreeRDP) il tocco arriva al programma remoto così com'è: FreeRDP non inoltra il multitocco in modo uniforme.";

    public override void Apply()
    {
        _settings.TouchMode = Mode.Value;
        _settings.SessionTouch = SessionMode.Value;
        var map = new TouchGestureMap(new Dictionary<TouchGesture, TouchActionKind>());
        foreach (var g in Gestures)
            map = map.With(g.Gesture, g.Action.Value);
        _settings.GestureMap = map;
    }
}
