using System.Drawing;
using System.Diagnostics;

namespace RdpManager.Core;

/// <summary>
/// Misure per l'uso col dito (dalla 1.6.2): bersagli di almeno 40 px logici (a 150% sono 60 px veri), dialoghi
/// sempre dentro l'area di lavoro anche su uno schermo piccolo (1366x912 al 150% = 911x608 px logici).
/// Solo calcoli: i controlli li usano da UI\TouchUi.
/// </summary>
public static class TouchMetrics
{
    /// <summary>Lato minimo di un bersaglio da toccare, in pixel logici (a 96 dpi).</summary>
    public const int MinTarget = 40;

    /// <summary>Lato minimo dei pulsanti della barra della sessione e della barra a schermo intero (usate col dito, spesso su tablet).</summary>
    public const int SessionTarget = 48;

    /// <summary>Distanza minima tra un dialogo e il bordo dell'area di lavoro, in pixel logici.</summary>
    public const int DialogMargin = 12;

    /// <summary>Tocco prolungato: dopo quanto vale come clic destro, e di quanto il dito può muoversi senza annullarlo (px logici).</summary>
    public static readonly TimeSpan LongPressHold = TimeSpan.FromMilliseconds(500);
    public const int LongPressTolerance = 10;

    /// <summary>Da pixel logici (96 dpi) a pixel veri per i dpi dati, arrotondando per eccesso.</summary>
    public static int Scale(int logical, int dpi) => (int)Math.Ceiling(logical * Math.Max(96, dpi) / 96.0);

    /// <summary>Spazio in più sopra e sotto per portare un elemento alto <paramref name="height"/> a <paramref name="minHeight"/>.</summary>
    public static int PaddingToReach(int height, int minHeight) => Math.Max(0, (minHeight - height + 1) / 2);

    /// <summary>Misura più grande che un dialogo può avere in quell'area di lavoro (in pixel veri).</summary>
    public static Size MaxDialogSize(Rectangle workArea, int dpi)
    {
        int margin = Scale(DialogMargin, dpi);
        return new Size(Math.Max(1, workArea.Width - 2 * margin), Math.Max(1, workArea.Height - 2 * margin));
    }

    /// <summary>La misura voluta, ridotta se non ci sta nell'area di lavoro.</summary>
    public static Size FitSize(Size desired, Rectangle workArea, int dpi)
    {
        var max = MaxDialogSize(workArea, dpi);
        return new Size(Math.Min(desired.Width, max.Width), Math.Min(desired.Height, max.Height));
    }

    /// <summary>Posizione di un dialogo di misura <paramref name="size"/>: al centro del proprietario, ma sempre dentro l'area di lavoro.</summary>
    public static Point Place(Size size, Rectangle? owner, Rectangle workArea)
    {
        var around = owner is { Width: > 0, Height: > 0 } o ? o : workArea;
        int x = around.X + (around.Width - size.Width) / 2;
        int y = around.Y + (around.Height - size.Height) / 2;
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height));
        return new Point(x, y);
    }
}

/// <summary>
/// Riconosce il tocco prolungato (tenere premuto al posto del clic destro): scatta una volta sola quando il dito
/// resta fermo (entro la tolleranza) per il tempo stabilito; se si muove troppo o si solleva prima, non scatta.
/// L'orologio lo passa chi chiama, così si prova senza aspettare.
/// </summary>
public sealed class LongPressDetector
{
    private readonly TimeSpan _hold;
    private readonly int _tolerance;
    private Point _origin;
    private DateTime _start;
    private bool _active;

    public LongPressDetector(TimeSpan? hold = null, int tolerance = TouchMetrics.LongPressTolerance)
    {
        _hold = hold ?? TouchMetrics.LongPressHold;
        _tolerance = tolerance;
    }

    /// <summary>Il dito è giù e non ha ancora annullato né fatto scattare il tocco prolungato.</summary>
    public bool IsArmed => _active;

    /// <summary>Il tocco prolungato è già scattato per la pressione in corso o appena finita (per ignorare il clic che segue).</summary>
    public bool Fired { get; private set; }

    /// <summary>Punto in cui è cominciata la pressione.</summary>
    public Point Origin => _origin;

    public void Press(Point location, DateTime now)
    {
        _origin = location;
        _start = now;
        _active = true;
        Fired = false;
    }

    /// <summary>Il dito si muove: oltre la tolleranza il tocco prolungato è annullato (è un trascinamento).</summary>
    public void Move(Point location)
    {
        if (!_active)
            return;
        int dx = location.X - _origin.X, dy = location.Y - _origin.Y;
        if (dx * dx + dy * dy > _tolerance * _tolerance)
            _active = false;
    }

    /// <summary>Il dito si solleva (o esce dal controllo).</summary>
    public void Release() => _active = false;

    /// <summary>Vero una sola volta, appena passato il tempo di pressione col dito fermo.</summary>
    public bool Poll(DateTime now)
    {
        if (!_active || now - _start < _hold)
            return false;
        _active = false;
        Fired = true;
        return true;
    }
}

/// <summary>Tastiera su schermo di Windows: prima quella a tocco (TabTip), altrimenti osk.exe.</summary>
public static class ScreenKeyboard
{
    /// <summary>Programmi da provare, nell'ordine.</summary>
    public static IReadOnlyList<string> Candidates(string commonProgramFiles, string systemDirectory) =>
    [
        Path.Combine(commonProgramFiles, "microsoft shared", "ink", "TabTip.exe"),
        Path.Combine(systemDirectory, "osk.exe"),
    ];

    /// <summary>
    /// Avvia il primo programma che esiste e parte. Restituisce il percorso avviato, null se nessuno ha funzionato.
    /// <paramref name="exists"/> e <paramref name="start"/> sono separati dal sistema per poterli provare.
    /// </summary>
    public static string? Open(IEnumerable<string> candidates, Func<string, bool> exists, Func<string, bool> start)
    {
        foreach (var path in candidates)
            if (exists(path) && start(path))
                return path;
        return null;
    }

    /// <summary>Apre la tastiera su schermo di questo PC. Falso se non c'è o non parte.</summary>
    public static bool TryOpen()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var candidates = Candidates(common, Environment.SystemDirectory);
        return Open(candidates, File.Exists, path =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                AppLog.Default?.Warning($"Tastiera su schermo: {path} non parte ({ex.Message})");
                return false;
            }
        }) is not null;
    }
}
