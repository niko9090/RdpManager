using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// «Connessione in corso…»: il controllo RDP è una finestra nativa a pieno riquadro e copre la scritta sotto di sé,
/// quindi durante la connessione la scritta deve stare in primo piano e sparire a connessione avvenuta.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class LoadingOverlayTests
{
    private static void OnSta(Action body) => StaTestRunner.Run(body);

    private static RdpSessionView NewView() => new(new RdpConnection { Name = "Prova", Host = "127.0.0.1", Port = 1 });

    [Fact]
    public void WhileConnecting_TheLoadingTextIsShownAboveTheRdpControl_AndGoesAwayWhenConnected()
    {
        OnSta(() =>
        {
            using var view = NewView();
            using var cover = new Panel(); // al posto del controllo RDP, che copre tutta la scheda
            view.AddCoverForTests(cover);

            view.SetStateForTests(SessionState.Connecting, "Connessione in corso…");
            Assert.True(view.LoadingOverlayOnTop, "la scritta di caricamento deve stare sopra il controllo RDP");
            Assert.Contains("Connessione in corso", view.MessageText);

            view.SetStateForTests(SessionState.Connected, "Connessa");
            Assert.False(view.LoadingOverlayOnTop);
            Assert.Equal("", view.MessageText);
        });
    }

    [Fact]
    public void WhenTheControlShowsItsOwnPrompt_TheLoadingTextStepsAside()
    {
        OnSta(() =>
        {
            using var view = NewView();
            using var cover = new Panel();
            view.AddCoverForTests(cover);
            view.SetStateForTests(SessionState.Connecting, "Connessione in corso…");
            Assert.True(view.LoadingOverlayOnTop);

            view.SetAuthenticationWarningForTests(true);
            view.SetStateForTests(SessionState.Connecting, "Avviso");
            Assert.False(view.LoadingOverlayOnTop, "non deve coprire l'avviso del certificato");
        });
    }
}
