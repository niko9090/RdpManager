using System.Net;
using System.Text;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Plancia VPN: client contro un server finto e modello che decide cosa mostra la finestra.</summary>
public class VpnDashboardTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw ex;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Elenco_letto_bene_con_il_token_dell_account()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """
            {"network":"10.66.0.0/24","devices":[
              {"name":"Casa","address":"10.66.0.2","connected":false,"lastContact":"2026-11-20T10:00:00+00:00"},
              {"name":"Ufficio","address":"10.66.0.3","connected":true,"lastContact":"2026-11-23T11:59:30+00:00"},
              {"name":"Nuovo","address":"10.66.0.4","connected":false,"lastContact":null}]}
            """));
        using var client = new VpnDashboardClient("https://sync.example.com:8443", handler);

        var devices = await client.GetDevicesAsync("tok123");

        Assert.Equal(3, devices.Count);
        Assert.Equal("Ufficio", devices[1].Name);
        Assert.Equal("10.66.0.3", devices[1].Address);
        Assert.True(devices[1].Connected);
        Assert.Null(devices[2].LastContact);
        Assert.Equal("Bearer tok123", handler.Last!.Headers.Authorization!.ToString());
        Assert.Equal("https://sync.example.com:8443/v1/vpn/overview", handler.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Elenco_porta_id_del_dispositivo_e_proprietario_se_il_server_li_da()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """
            {"devices":[
              {"name":"Casa","address":"10.66.0.2","connected":true,"lastContact":null,"deviceId":"dev-7","owner":"mario"},
              {"name":"Vecchio","address":"10.66.0.3","connected":false,"lastContact":null}]}
            """));
        using var client = new VpnDashboardClient("https://sync.example.com", handler);

        var devices = await client.GetDevicesAsync("t");

        Assert.Equal("dev-7", devices[0].DeviceId);
        Assert.Equal("mario", devices[0].Owner);
        Assert.Null(devices[1].DeviceId); // server vecchio: nessun campo, nessun errore
        Assert.Null(devices[1].Owner);
    }

    [Fact]
    public async Task Senza_accesso_401_diventa_unauthorized()
    {
        using var client = new VpnDashboardClient("https://sync.example.com", new FakeHandler(_ =>
            Json(HttpStatusCode.Unauthorized, """{"error":"unauthorized","message":"Accesso richiesto."}""")));

        var ex = await Assert.ThrowsAsync<SyncServerException>(() => client.GetDevicesAsync("falso"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal("unauthorized", ex.Code);
    }

    [Fact]
    public async Task Server_non_raggiungibile()
    {
        using var client = new VpnDashboardClient("https://sync.example.com", new ThrowingHandler(new HttpRequestException("giù")));

        var ex = await Assert.ThrowsAsync<SyncUnreachableException>(() => client.GetDevicesAsync("t"));

        Assert.False(ex.ServerDown);
    }

    [Fact]
    public async Task Proxy_acceso_ma_servizio_spento_502()
    {
        using var client = new VpnDashboardClient("https://sync.example.com", new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var ex = await Assert.ThrowsAsync<SyncUnreachableException>(() => client.GetDevicesAsync("t"));

        Assert.True(ex.ServerDown);
    }

    [Fact]
    public async Task Risposta_senza_il_formato_atteso_e_un_errore_chiaro()
    {
        using var client = new VpnDashboardClient("https://sync.example.com", new FakeHandler(_ => Json(HttpStatusCode.OK, "[1,2]")));

        var ex = await Assert.ThrowsAsync<SyncServerException>(() => client.GetDevicesAsync("t"));

        Assert.Equal("invalid_response", ex.Code);
    }

    [Fact]
    public void Il_modello_ordina_i_collegati_per_primi_e_scrive_in_italiano()
    {
        var view = VpnDashboardModel.FromDevices(
        [
            new VpnDashboardDevice("Zeta", "10.66.0.9/32", false, null),
            new VpnDashboardDevice("beta", "10.66.0.3", false, Now.AddHours(-3)),
            new VpnDashboardDevice("Alfa", "10.66.0.2", true, Now.AddSeconds(-20)),
        ], Now);

        Assert.Equal(VpnDashboardStatus.Ok, view.Status);
        Assert.Equal(["Alfa", "beta", "Zeta"], view.Rows.Select(r => r.Name));
        Assert.Equal("Collegato", view.Rows[0].StateText);
        Assert.Equal("adesso", view.Rows[0].LastContactText);
        Assert.Equal("Non collegato", view.Rows[1].StateText);
        Assert.Equal("3 h fa", view.Rows[1].LastContactText);
        Assert.Equal("mai", view.Rows[2].LastContactText);
        Assert.Equal("10.66.0.9", view.Rows[2].Address); // senza «/32»
        Assert.All(view.Rows, r => Assert.True(VpnDashboardView.CanOpen(r)));
    }

    [Fact]
    public void Il_modello_senza_pc_e_senza_account()
    {
        var empty = VpnDashboardModel.FromDevices([], Now);
        Assert.Equal(VpnDashboardStatus.Empty, empty.Status);
        Assert.Empty(empty.Rows);

        var none = VpnDashboardModel.NotSignedIn();
        Assert.Equal(VpnDashboardStatus.NotSignedIn, none.Status);
        Assert.Contains("account", none.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized", VpnDashboardStatus.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "vpn_disabled", VpnDashboardStatus.Disabled)]
    [InlineData(HttpStatusCode.InternalServerError, "error", VpnDashboardStatus.Error)]
    public void Il_modello_traduce_gli_errori_del_server(HttpStatusCode status, string code, VpnDashboardStatus expected)
    {
        var view = VpnDashboardModel.FromError(new SyncServerException(status, code, "x"));

        Assert.Equal(expected, view.Status);
        Assert.NotEmpty(view.Message);
        Assert.Empty(view.Rows);
    }

    [Fact]
    public void Il_modello_dice_se_il_server_non_risponde_o_e_spento()
    {
        var down = VpnDashboardModel.FromError(new SyncUnreachableException("x"));
        var off = VpnDashboardModel.FromError(new SyncUnreachableException("x", serverDown: true));

        Assert.Equal(VpnDashboardStatus.Unreachable, down.Status);
        Assert.Contains("non risponde", down.Message);
        Assert.Equal(VpnDashboardStatus.Unreachable, off.Status);
        Assert.Contains("spento", off.Message);
    }

    [Fact]
    public void L_aggiornamento_e_ogni_30_secondi()
    {
        Assert.Equal(30, VpnDashboardModel.RefreshSeconds);
    }
}
