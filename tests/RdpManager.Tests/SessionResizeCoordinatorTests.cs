using System.Drawing;
using RdpManager.Core;

namespace RdpManager.Tests;

public class SessionResizeCoordinatorTests
{
    private sealed class FakeTimer : IResizeTimer
    {
        public int? DueIn;
        public event Action? Elapsed;
        public void Restart(int delayMs) => DueIn = delayMs;
        public void Stop() => DueIn = null;
        public void Fire()
        {
            Assert.NotNull(DueIn);
            DueIn = null;
            Elapsed?.Invoke();
        }
    }

    private sealed class FakeTarget : IResizeTarget
    {
        public bool IsConnected { get; set; } = true;
        public bool IsShown { get; set; } = true;
        public ResolutionMode Mode { get; set; } = ResolutionMode.FitToWindow;
        public Size ClientSize { get; set; } = new(1280, 720);
        public int Scale { get; set; } = 100;
        public List<(Size, int)> Updates = [];
        public int FailNext;
        public bool Throw;
        public int SmartCalls;
        public bool GaveUp;

        public bool TryUpdate(Size size, int scale)
        {
            if (FailNext > 0)
            {
                FailNext--;
                if (Throw)
                    throw new InvalidOperationException();
                return false;
            }
            Updates.Add((size, scale));
            return true;
        }

        public void UpdateSmartSizing() => SmartCalls++;
        public void GiveUp() => GaveUp = true;
    }

    private static (SessionResizeCoordinator c, FakeTarget t, FakeTimer timer) Make()
    {
        var t = new FakeTarget();
        var timer = new FakeTimer();
        var c = new SessionResizeCoordinator(t, timer);
        c.SetBaseline(new Size(1000, 600), 100);
        return (c, t, timer);
    }

    [Fact]
    public void Aggiorna_dopo_il_ritardo_dall_ultimo_evento_con_la_dimensione_finale()
    {
        var (c, t, timer) = Make();
        c.Request();
        c.Request();
        Assert.Equal(SessionResizeCoordinator.DelayMs, timer.DueIn);
        Assert.InRange(SessionResizeCoordinator.DelayMs, 300, 500);
        Assert.Empty(t.Updates);
        t.ClientSize = new Size(1500, 900);
        timer.Fire();
        Assert.Equal([(new Size(1500, 900), 100)], t.Updates);
        Assert.False(c.Pending);
    }

    [Fact]
    public void Sessione_non_connessa_resta_in_sospeso_e_parte_con_la_richiesta_successiva()
    {
        var (c, t, timer) = Make();
        t.IsConnected = false;
        c.Request();
        timer.Fire();
        Assert.Empty(t.Updates);
        Assert.True(c.Pending);
        t.IsConnected = true; // login finito
        c.Request();
        timer.Fire();
        Assert.Single(t.Updates);
    }

    [Fact]
    public void Scheda_nascosta_non_aggiorna_e_lo_fa_quando_torna_visibile()
    {
        var (c, t, timer) = Make();
        t.IsShown = false;
        c.Request();
        timer.Fire();
        Assert.Empty(t.Updates);
        t.IsShown = true;
        c.Request();
        timer.Fire();
        Assert.Single(t.Updates);
    }

    [Fact]
    public void Cambio_di_scala_senza_cambio_di_dimensione_aggiorna()
    {
        var (c, t, timer) = Make();
        t.ClientSize = new Size(1000, 600);
        c.Request();
        timer.Fire();
        Assert.Empty(t.Updates); // niente di nuovo
        t.Scale = 150;
        c.Request();
        timer.Fire();
        Assert.Equal([(new Size(1000, 600), 150)], t.Updates);
    }

    [Fact]
    public void Un_fallimento_si_riprova_e_poi_riesce()
    {
        var (c, t, timer) = Make();
        t.FailNext = 2;
        t.Throw = true;
        c.Request();
        timer.Fire();
        Assert.Equal(SessionResizeCoordinator.RetryDelayMs, timer.DueIn);
        timer.Fire();
        Assert.NotNull(timer.DueIn);
        timer.Fire();
        Assert.Single(t.Updates);
        Assert.False(c.GaveUp);
        Assert.False(c.Pending);
    }

    [Fact]
    public void Dopo_troppi_fallimenti_si_scala_l_immagine_e_la_nuova_connessione_riparte()
    {
        var (c, t, timer) = Make();
        t.FailNext = 100;
        c.Request();
        for (int i = 0; i < SessionResizeCoordinator.MaxFailures; i++)
            timer.Fire();
        Assert.True(t.GaveUp);
        Assert.True(c.GaveUp);
        Assert.Null(timer.DueIn);

        t.FailNext = 0;
        c.Request();
        timer.Fire();
        Assert.Empty(t.Updates); // rinunciato: non riprova finché non si riconnette
        c.Reset();
        c.Request();
        timer.Fire();
        Assert.Single(t.Updates);
    }

    [Fact]
    public void Risoluzione_fissa_aggiorna_solo_lo_zoom()
    {
        var (c, t, timer) = Make();
        t.Mode = ResolutionMode.Fixed;
        c.Request();
        timer.Fire();
        Assert.Equal(1, t.SmartCalls);
        Assert.Empty(t.Updates);
    }

    [Fact]
    public void La_dimensione_e_limitata_e_resa_pari()
    {
        var (c, t, timer) = Make();
        t.ClientSize = new Size(101, 50);
        c.Request();
        timer.Fire();
        Assert.Equal(new Size(640, 480), t.Updates[0].Item1);
        Assert.Equal(new Size(1280, 720), SessionResizeCoordinator.Clamp(new Size(1281, 720)));
    }
}
