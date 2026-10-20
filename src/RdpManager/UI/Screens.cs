using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>I monitor di WinForms tradotti nel modello del Core (<see cref="ScreenInfo"/>), con il DPI di ciascuno.</summary>
internal static class Screens
{
    public static IReadOnlyList<ScreenInfo> All() =>
        Screen.AllScreens.Select(s => new ScreenInfo(s.DeviceName, s.Bounds, s.WorkingArea, s.Primary,
            NativeMethods.DpiAt(new Point(s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2)))).ToList();

    /// <summary>"1 · 2560×1440 (principale, 150%)": numero in ordine da sinistra, risoluzione e scala.</summary>
    public static string Describe(ScreenInfo s, IReadOnlyList<ScreenInfo> all)
    {
        int number = MonitorLayout.Ordered(all).FindIndex(x => x.DeviceName == s.DeviceName) + 1;
        var extra = new List<string>();
        if (s.Primary)
            extra.Add("principale");
        extra.Add($"{MonitorLayout.DesktopScaleFactor(s.Dpi)}%");
        return $"{number} · {s.Bounds.Width}×{s.Bounds.Height} ({string.Join(", ", extra)})";
    }

    public static ScreenInfo? Of(Form form) => MonitorLayout.ScreenOf(All(), form.Bounds);

    /// <summary>Sposta una finestra (non massimizzata) sul monitor indicato, scalando con il rapporto dei DPI.</summary>
    public static void MoveForm(Form form, ScreenInfo to)
    {
        var all = All();
        var from = MonitorLayout.ScreenOf(all, form.Bounds) ?? to;
        bool maximized = form.WindowState == FormWindowState.Maximized;
        if (maximized)
            form.WindowState = FormWindowState.Normal;
        var normal = form.WindowState == FormWindowState.Minimized ? form.RestoreBounds : form.Bounds;
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        form.Bounds = MonitorLayout.MoveToScreen(normal, from, to);
        if (maximized)
            form.WindowState = FormWindowState.Maximized;
    }

    /// <summary>Sposta la finestra sul monitor successivo (da sinistra a destra, ricominciando dal primo).</summary>
    public static ScreenInfo? MoveFormToNext(Form form, bool forward = true)
    {
        var all = All();
        if (all.Count < 2)
            return null;
        var next = MonitorLayout.NextScreen(all, MonitorLayout.ScreenOf(all, form.Bounds), forward);
        if (next is not null)
            MoveForm(form, next);
        return next;
    }
}
