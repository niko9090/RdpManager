using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.Sessions;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

/// <summary>
/// Da Linux i PC della VPN non si aprivano: FreeRDP usciva con il codice 159 (nessuna credenziale) perché la connessione
/// non aveva una password leggibile su questo computer (mai salvata, o cifrata su Windows). FreeRDP senza terminale non la
/// chiede: ora la chiede l'app e riprova.
/// </summary>
public class CredentialsPromptTests
{
    private static FreeRdpSession Session(RdpConnection c) =>
        new(c, new FreeRdpClient("xfreerdp3", true, false), null, _ => Task.FromResult<string?>("prova: niente avvio"), a => a());

    [Theory]
    [InlineData(159)]
    [InlineData(154)]
    [InlineData(132)]
    [InlineData(134)]
    public void Credenziali_mancanti_o_rifiutate_si_riconoscono(int code)
    {
        Assert.True(CertificateTrust.IsCredentialsProblem(code));
        Assert.False(CertificateTrust.IsRetryableExit(code, ""));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(131)]
    [InlineData(143)]
    public void Altri_codici_non_chiedono_credenziali(int code) => Assert.False(CertificateTrust.IsCredentialsProblem(code));

    [Fact]
    public async Task Con_il_codice_159_chiede_utente_e_password_e_riparte_con_quelle()
    {
        var c = new RdpConnection { Name = "Z2", Host = "10.66.0.4", Username = "tecnico" };
        var asked = new List<CredentialsPrompt>();
        (Guid Id, CredentialsAnswer Answer)? saved = null;
        var session = Session(c);
        session.CredentialsDecider = p => { asked.Add(p); return Task.FromResult<CredentialsAnswer?>(new("tecnico", "audio", "pw", true)); };
        session.CredentialsSaver = (id, a) => saved = (id, a);

        session.HandleExit(159, "");
        await Task.Delay(50);

        var prompt = Assert.Single(asked);
        Assert.Equal("tecnico", prompt.Username);
        Assert.Contains("password salvata", prompt.Reason);
        var launch = session.ConnectionForLaunch();
        Assert.Equal("pw", launch.Password);
        Assert.Equal("audio", launch.Domain);
        Assert.Null(c.Password); // l'originale non cambia: lo salva chi riceve CredentialsSaver
        Assert.Equal(c.Id, saved!.Value.Id);
    }

    [Fact]
    public async Task Annullando_la_sessione_finisce_con_un_messaggio_chiaro()
    {
        var session = Session(new RdpConnection { Name = "Z2", Host = "10.66.0.4" });
        session.CredentialsDecider = _ => Task.FromResult<CredentialsAnswer?>(null);
        session.HandleExit(159, "");
        await Task.Delay(50);
        Assert.Contains("utente e password", session.Message);
        Assert.Null(session.ConnectionForLaunch().Password);
    }

    [Fact]
    public void Senza_chi_chiede_la_sessione_finisce_con_il_codice()
    {
        var session = Session(new RdpConnection { Name = "Z2", Host = "10.66.0.4" });
        session.HandleExit(159, "");
        Assert.Contains("159", session.Message);
    }

    [Fact]
    public void Le_credenziali_ricordate_vanno_nella_connessione_e_si_salvano()
    {
        using var data = new TempData();
        var profile = new CredentialProfile { Name = "Ufficio", Username = "altro", Password = null };
        var conn = Seed.Conn("Z2", "10.66.0.4");
        conn.CredentialProfileId = profile.Id;
        conn.ProtectedPassword = "cifrata-su-windows";
        Seed.Write(data.Store, conn);
        var vm = Seed.Main(data, new FakeDialogs());
        vm.Document.CredentialProfiles.Add(profile);

        vm.SaveCredentials(conn.Id, new CredentialsAnswer("tecnico", "audio", "pw", true));

        var reloaded = data.Store.Load().Connections.Single(c => c.Id == conn.Id);
        Assert.Equal("tecnico", reloaded.Username);
        Assert.Equal("audio", reloaded.Domain);
        Assert.Equal("pw", reloaded.Password);
        Assert.Null(reloaded.CredentialProfileId);
        Assert.Equal("pw", vm.Document.ResolveCredentials(vm.Document.Connections.Single(c => c.Id == conn.Id)).Password);
        Assert.Equal("altro", profile.Username); // il profilo condiviso non si tocca
    }
}

public class ExitExplanationTests
{
    private static FreeRdpSession Session() =>
        new(new RdpConnection { Name = "Z2", Host = "10.66.0.4" }, new FreeRdpClient("xfreerdp3", true, false), null,
            _ => Task.FromResult<string?>("prova"), a => a());

    [Fact]
    public void Il_codice_147_dice_che_il_PC_non_risponde() =>
        Assert.Contains("non risponde", FreeRdpExitCodes.Describe(147).Meaning);

    [Theory]
    [InlineData(5)]
    [InlineData(133)]
    [InlineData(147)]
    [InlineData(148)]
    [InlineData(159)]
    [InlineData(255)]
    [InlineData(999)]
    public void Ogni_codice_ha_spiegazione_e_cosa_fare(int code)
    {
        var info = FreeRdpExitCodes.Describe(code);
        Assert.False(string.IsNullOrWhiteSpace(info.Meaning));
        Assert.False(string.IsNullOrWhiteSpace(info.Hint));
    }

    [Fact]
    public void Con_un_errore_si_apre_la_finestra_con_codice_spiegazione_e_dettaglio()
    {
        var shown = new List<SessionErrorInfo>();
        var session = Session();
        session.ErrorReporter = shown.Add;
        session.HandleExit(147, "[ERROR][com.freerdp.core] - transport_connect failed\nERRCONNECT_CONNECT_TRANSPORT_FAILED");
        var e = Assert.Single(shown);
        Assert.Equal(147, e.ExitCode);
        Assert.Equal("10.66.0.4", e.Host);
        Assert.Contains("TRANSPORT_FAILED", e.Detail);
        Assert.Contains("non risponde", session.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Chiusure_normali_non_aprono_la_finestra(int code)
    {
        var shown = new List<SessionErrorInfo>();
        var session = Session();
        session.ErrorReporter = shown.Add;
        session.HandleExit(code, "");
        Assert.Empty(shown);
    }
}
