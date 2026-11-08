using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace RdpManager.Linux.Sessions;

/// <summary>Vista della scheda FreeRDP: superficie X11 per l'incorporamento, oppure avviso quando la sessione è in una finestra separata o disconnessa.</summary>
public sealed partial class FreeRdpSessionView : UserControl
{
    private bool _decided;

    public FreeRdpSessionView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Decide();
    }

    private void Decide()
    {
        if (_decided || DataContext is not FreeRdpSession session)
            return;
        _decided = true;

        if (!session.WantsEmbedding || !X11EmbedHost.IsX11(TopLevel.GetTopLevel(this)))
        {
            // Wayland nativo, client SDL o più monitor: finestra separata collegata alla scheda.
            session.StartDetached();
            return;
        }

        var host = new X11EmbedHost();
        host.Created += xid =>
        {
            // Si parte quando la superficie ha una dimensione vera (altrimenti /size sarebbe quella provvisoria).
            void TryStart()
            {
                var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                var w = (int)(host.Bounds.Width * scale);
                var h = (int)(host.Bounds.Height * scale);
                if (w < 200 || h < 200)
                    return;
                host.SizeChanged -= OnSize;
                session.AttachSurface(xid, w, h);
            }
            void OnSize(object? s, SizeChangedEventArgs e) => TryStart();
            host.SizeChanged += OnSize;
            TryStart();
        };
        SurfaceHost.Children.Add(host);

        // Se dopo qualche secondo la superficie non è nata (X11 senza libX11, ecc.) si ripiega sulla finestra separata.
        DispatcherTimer.RunOnce(() =>
        {
            if (!session.IsEmbedded)
            {
                SurfaceHost.Children.Remove(host);
                session.StartDetached();
            }
        }, TimeSpan.FromSeconds(4));
    }
}
