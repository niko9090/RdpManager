using RdpManager.Core;
using RdpManager.Linux.Features;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Una scorciatoia modificabile: una riga della pagina «Scorciatoie».</summary>
public sealed class ShortcutEntry : ViewModelBase
{
    private string _text;

    public ShortcutEntry(ShortcutAction action, KeyCombo combo)
    {
        Action = action;
        _text = combo.ToString();
    }

    public ShortcutAction Action { get; }

    public string Description => ShortcutMap.Describe(Action);

    /// <summary>Combinazione scritta («Ctrl+Alt+Destra»); vuota = azione disattivata.</summary>
    public string Text { get => _text; set => Set(ref _text, value); }

    public bool IsValid => string.IsNullOrWhiteSpace(Text) || KeyCombo.TryParse(Text, out _);
}

/// <summary>Pagina «Scorciatoie» (blocco B): le combinazioni dell'app, con controllo dei conflitti.</summary>
public sealed class ScorciatoiePageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private string _problems = "";

    public ScorciatoiePageViewModel(AppSettings settings)
    {
        _settings = settings;
        var map = ShortcutMap.FromSettings(settings.Shortcuts);
        Entries = Enum.GetValues<ShortcutAction>().Select(a => new ShortcutEntry(a, map[a])).ToList();
        foreach (var e in Entries)
            e.PropertyChanged += (_, _) => Check();
        ResetCommand = new RelayCommand(Reset);
        Check();
    }

    public override string Title => "Scorciatoie";

    public IReadOnlyList<ShortcutEntry> Entries { get; }

    public RelayCommand ResetCommand { get; }

    /// <summary>Combinazioni non valide o usate due volte (vuoto se va tutto bene).</summary>
    public string Problems { get => _problems; private set => Set(ref _problems, value); }

    public string Note => "Si scrivono come «Ctrl+Alt+Destra» o «Ctrl+Alt+Maiusc+1»; vuoto disattiva l'azione. "
        + "Le combinazioni valgono quando la finestra dell'applicazione è attiva; F1, F5, F11, Ctrl+F4, Ctrl+Tab e Ctrl+Maiusc+D sono fisse.";

    private ShortcutMap Build()
    {
        var map = new ShortcutMap();
        foreach (var e in Entries)
            map[e.Action] = KeyCombo.TryParse(e.Text, out var combo) ? combo : default;
        return map;
    }

    private void Check()
    {
        var lines = new List<string>();
        foreach (var e in Entries.Where(e => !e.IsValid))
            lines.Add($"«{e.Text}» non è una combinazione valida ({e.Description}).");
        foreach (var group in Build().Conflicts())
            lines.Add("Stessa combinazione per: " + string.Join(", ", group.Select(ShortcutMap.Describe)) + ".");
        Problems = string.Join('\n', lines);
    }

    private void Reset()
    {
        foreach (var e in Entries)
            e.Text = ShortcutMap.Defaults.TryGetValue(e.Action, out var c) ? c.ToString() : "";
    }

    public override void Apply()
    {
        // Le righe non valide tengono il valore salvato.
        var current = ShortcutMap.FromSettings(_settings.Shortcuts);
        foreach (var e in Entries)
        {
            if (!e.IsValid)
                continue;
            current[e.Action] = KeyCombo.TryParse(e.Text, out var combo) ? combo : default;
        }
        _settings.Shortcuts = current.ToSettings();
    }
}
