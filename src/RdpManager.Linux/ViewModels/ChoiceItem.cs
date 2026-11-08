namespace RdpManager.Linux.ViewModels;

/// <summary>Voce di un elenco a scelta con il testo italiano da mostrare (ToString è quello che vede la casella combinata).</summary>
public sealed record ChoiceItem<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

public static class Choices
{
    public static IReadOnlyList<ChoiceItem<Core.ResolutionMode>> Resolution { get; } =
    [
        new(Core.ResolutionMode.FitToWindow, "Adatta alla finestra"),
        new(Core.ResolutionMode.Fixed, "Risoluzione fissa"),
    ];

    public static IReadOnlyList<ChoiceItem<Core.AudioMode>> Audio { get; } =
    [
        new(Core.AudioMode.Local, "Riproduci su questo computer"),
        new(Core.AudioMode.Remote, "Lascia sul computer remoto"),
        new(Core.AudioMode.None, "Nessun audio"),
    ];

    public static IReadOnlyList<ChoiceItem<Core.QualityProfile>> Quality { get; } =
    [
        new(Core.QualityProfile.Automatic, "Automatica"),
        new(Core.QualityProfile.LocalNetwork, "Rete locale"),
        new(Core.QualityProfile.SlowConnection, "Connessione lenta"),
    ];

    public static IReadOnlyList<ChoiceItem<Core.AppTheme>> Theme { get; } =
    [
        new(Core.AppTheme.System, "Come il sistema"),
        new(Core.AppTheme.Light, "Chiaro"),
        new(Core.AppTheme.Dark, "Scuro"),
    ];

    public static IReadOnlyList<ChoiceItem<Core.UpdateMode>> Update { get; } =
    [
        new(Core.UpdateMode.AutomaticWithConfirmation, "Automatici, con conferma"),
        new(Core.UpdateMode.NotifyOnly, "Solo avviso"),
        new(Core.UpdateMode.Off, "Disattivati"),
    ];

    public static IReadOnlyList<ChoiceItem<Core.ConnectionOrder>> Order { get; } =
    [
        new(Core.ConnectionOrder.Alphabetical, "In ordine alfabetico"),
        new(Core.ConnectionOrder.Manual, "Ordine manuale"),
    ];

    public static ChoiceItem<T> Pick<T>(IReadOnlyList<ChoiceItem<T>> list, T value) =>
        list.FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value)) ?? list[0];
}
