using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>Menu laterale a schermo intero per il dito: voci alte almeno 48 px logici, icone, intestazioni, scorrimento, chiusura.</summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class SideBarTouchTests
{
    private static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "La prova UI non è terminata");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static (Form form, FullScreenSideBar bar) Make(BarPosition position, int connections, int height = 600)
    {
        var form = new Form { ClientSize = new Size(position == BarPosition.Bottom ? 1000 : 300, height), StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false };
        var bar = new FullScreenSideBar { Dock = position == BarPosition.Bottom ? DockStyle.Bottom : DockStyle.Fill };
        if (position == BarPosition.Bottom)
            bar.Height = height;
        form.Controls.Add(bar);
        form.Show();
        var list = Enumerable.Range(1, connections).Select(i => new RdpConnection { Name = $"Pc {i}", Host = $"host{i}.invalid" }).ToList();
        var settings = new FullScreenBarSettings { Position = position, Mode = BarMode.Fixed };
        bar.Rebuild(new FullScreenSideBar.Content(settings, [], list.Take(2).ToList(), list, [], [new QuickText { Name = "Uno", Text = "uno" }],
            new ShortcutMap(), true, false));
        Application.DoEvents();
        return (form, bar);
    }

    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));

    [Fact]
    public void Items_AreAtLeast48LogicalPixelsTall_WithAnIconAndSpacing()
    {
        Run(() =>
        {
            var (form, bar) = Make(BarPosition.Left, 4);
            using (form)
            {
                int min = TouchMetrics.Scale(TouchMetrics.SessionTarget, bar.DeviceDpi);
                var buttons = All(bar).OfType<FullScreenSideBar.BarButton>().Where(b => b != bar.CloseButton).ToList();
                Assert.NotEmpty(buttons);
                foreach (var b in buttons)
                {
                    Assert.True(b.Height >= min, $"«{b.Text}» alto {b.Height} < {min}");
                    Assert.True(b.Margin.Bottom >= TouchMetrics.Scale(6, bar.DeviceDpi), $"«{b.Text}» troppo vicino al successivo");
                    Assert.NotEmpty(b.Glyph);
                }
                Assert.True(bar.CloseButton.Height >= min);
            }
        });
    }

    [Fact]
    public void CloseButton_IsOnTopOfVerticalBars_AndAsksToHideTheBar()
    {
        Run(() =>
        {
            var (form, bar) = Make(BarPosition.Left, 2);
            using (form)
            {
                Assert.True(bar.CloseButton.Visible);
                SessionBarAction? asked = null;
                bar.SessionActionClicked += (_, a) => asked = a;
                bar.CloseButton.PerformClick();
                Assert.Equal(SessionBarAction.HideBar, asked);
            }
        });
    }

    [Fact]
    public void DraggingTheFinger_ScrollsTheBar_AndSwallowsTheClick()
    {
        Run(() =>
        {
            var (form, bar) = Make(BarPosition.Left, 6, height: 400);
            using (form)
            {
                var flow = All(bar).OfType<FlowLayoutPanel>().First(f => f.Parent == bar);
                Assert.True(flow.VerticalScroll.Visible, "Le voci devono superare l'altezza della barra");
                var button = All(flow).OfType<FullScreenSideBar.BarButton>().First(b => b.Tag is not null || b.Glyph.Length > 0);
                int clicks = 0;
                button.Click += (_, _) => clicks++;

                bar.PanBegin(button, 300);
                bar.PanMove(300 - 120); // dito verso l'alto: la barra scorre verso il fondo
                bar.PanEnd();
                Assert.True(bar.PanActive);
                Assert.True(-flow.AutoScrollPosition.Y > 0, "La barra deve essersi mossa");
                button.PerformClick();
                Assert.Equal(0, clicks); // il clic dopo il trascinamento non conta

                bar.PanBegin(button, 300); // un tocco semplice
                bar.PanEnd();
                button.PerformClick();
                Assert.Equal(1, clicks);
            }
        });
    }

    [Fact]
    public void GroupHeaders_AreShownForEverySection_AndBottomBarHasNoCloseHeader()
    {
        Run(() =>
        {
            var (form, bar) = Make(BarPosition.Bottom, 2);
            using (form)
            {
                Assert.False(bar.CloseButton.Visible);
                Assert.True(bar.ShownSections.Count >= 3);
                var headers = All(bar).OfType<Label>().Where(l => l.Text.Length > 0 && l.Text == l.Text.ToUpperInvariant()).ToList();
                Assert.True(headers.Count >= bar.ShownSections.Count - 1, "Ogni gruppo deve avere la sua intestazione");
            }
        });
    }
}
