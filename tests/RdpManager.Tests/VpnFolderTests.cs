using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Cartella automatica «marshmelloVPN»: creazione, abbinamento con le connessioni salvate, aggiornamento, PC tolto.</summary>
public class VpnFolderTests
{
    private static VpnDashboardDevice Dev(string name, string address) => new(name, address, true, null);

    private static RdpConnection Saved(string host, string user, string password = "pw", string name = "", string domain = "") =>
        new() { Name = name, Host = host, Username = user, Domain = domain, Password = password };

    private static List<RdpConnection> Auto(ConnectionsDocument doc) => doc.Connections.Where(c => c.AutoVpnDevice is not null).ToList();

    [Fact]
    public void Crea_la_cartella_con_una_connessione_per_pc_e_l_ip_della_vpn()
    {
        var doc = new ConnectionsDocument();
        Assert.True(VpnFolder.Apply(doc, [Dev("PC-UFFICIO", "10.8.0.2/24"), Dev("PC-CASA", "10.8.0.3")]));

        Assert.Contains(VpnFolder.FolderName, doc.AllGroups());
        var auto = Auto(doc);
        Assert.Equal(2, auto.Count);
        Assert.All(auto, c => Assert.Equal(VpnFolder.FolderName, c.Group));
        Assert.Equal("10.8.0.2", auto.Single(c => c.Name == "PC-UFFICIO").Host);
        Assert.Equal(VpnFolder.StableId("pc-ufficio"), auto.Single(c => c.Name == "PC-UFFICIO").Id);
    }

    [Fact]
    public void Senza_pc_non_fa_niente()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new RdpConnection { Name = "x", Host = "h", AutoVpnDevice = "x" });
        Assert.False(VpnFolder.Apply(doc, []));
        Assert.Single(doc.Connections);
        Assert.Empty(doc.Groups);
    }

    [Fact]
    public void Abbinamento_sicuro_riprende_accessi_e_impostazioni_senza_toccare_l_originale()
    {
        var doc = new ConnectionsDocument();
        var original = Saved("pc-ufficio.lan", "mario", "segreto", domain: "AZIENDA");
        original.RedirectDrives = true;
        original.Quality = QualityProfile.Automatic;
        original.DesktopWidth = 1280;
        doc.Connections.Add(original);

        VpnFolder.Apply(doc, [Dev("PC-UFFICIO", "10.8.0.2")]);

        var copy = Auto(doc).Single();
        Assert.Equal("mario", copy.Username);
        Assert.Equal("AZIENDA", copy.Domain);
        Assert.Equal("segreto", copy.Password);
        Assert.True(copy.RedirectDrives);
        Assert.Equal(1280, copy.DesktopWidth);
        Assert.Equal("10.8.0.2", copy.Host);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("pc-ufficio.lan", original.Host);
        Assert.Null(original.AutoVpnDevice);
        Assert.Equal("", original.Group);
    }

    [Theory]
    [InlineData("10.8.0.2", "")]
    [InlineData("pc-ufficio", "")]
    [InlineData("PC-Ufficio.lan", "")]
    [InlineData("altro", "pc-ufficio")]
    public void Lo_stesso_pc_si_riconosce_da_ip_nome_o_nome_della_connessione_senza_maiuscole(string host, string name)
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(Saved(host, "mario", name: name));
        VpnFolder.Apply(doc, [Dev("PC-UFFICIO", "10.8.0.2")]);
        Assert.Equal("mario", Auto(doc).Single().Username);
    }

    [Fact]
    public void Nessun_abbinamento_lascia_la_connessione_senza_accessi()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(Saved("altro-pc", "mario"));
        doc.Connections.Add(Saved("10.8.0.99", "luigi"));
        VpnFolder.Apply(doc, [Dev("PC-UFFICIO", "10.8.0.2")]);
        var copy = Auto(doc).Single();
        Assert.Equal("", copy.Username);
        Assert.False(copy.HasPassword);
    }

    [Fact]
    public void Abbinamento_dubbio_con_accessi_diversi_non_copia_niente()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(Saved("10.8.0.2", "mario"));
        doc.Connections.Add(Saved("pc-ufficio", "luigi"));
        VpnFolder.Apply(doc, [Dev("PC-UFFICIO", "10.8.0.2")]);
        var copy = Auto(doc).Single();
        Assert.Equal("", copy.Username);
        Assert.False(copy.HasPassword);
    }

    [Fact]
    public void Candidati_con_gli_stessi_accessi_non_sono_un_dubbio()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(Saved("10.8.0.2", "mario"));
        doc.Connections.Add(Saved("pc-ufficio", "MARIO"));
        VpnFolder.Apply(doc, [Dev("PC-UFFICIO", "10.8.0.2")]);
        Assert.Equal("mario", Auto(doc).Single().Username.ToLowerInvariant());
    }

    [Fact]
    public void Connessione_che_corrisponde_a_due_pc_diversi_si_scarta()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(Saved("10.8.0.3", "mario", name: "pc-ufficio"));
        VpnFolder.Apply(doc, [Dev("PC-UFFICIO", "10.8.0.2"), Dev("PC-CASA", "10.8.0.3")]);
        Assert.All(Auto(doc), c => Assert.Equal("", c.Username));
    }

    [Fact]
    public void Aggiornamento_aggiunge_i_nuovi_cambia_l_ip_e_non_crea_doppioni()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        Assert.False(VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]));

        Assert.True(VpnFolder.Apply(doc, [Dev("pc-a", "10.8.0.9"), Dev("PC-B", "10.8.0.4")]));
        var auto = Auto(doc);
        Assert.Equal(2, auto.Count);
        Assert.Equal("10.8.0.9", auto.Single(c => c.Name == "PC-A").Host);
        Assert.Equal(auto.Count, auto.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void L_aggiornamento_non_sovrascrive_gli_accessi_scelti_dall_utente_sulla_connessione_automatica()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        Auto(doc).Single().Username = "mio";
        doc.Connections.Add(Saved("pc-a", "altro"));
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        Assert.Equal("mio", Auto(doc).Single().Username);
    }

    [Fact]
    public void Gli_accessi_arrivano_quando_la_connessione_salvata_compare_dopo()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        doc.Connections.Add(Saved("pc-a", "mario"));
        Assert.True(VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]));
        Assert.Equal("mario", Auto(doc).Single().Username);
    }

    [Fact]
    public void Pc_tolto_dalla_rete_toglie_solo_la_connessione_automatica()
    {
        var doc = new ConnectionsDocument();
        var mine = Saved("10.8.0.3", "mario", name: "PC-B");
        doc.Connections.Add(mine);
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2"), Dev("PC-B", "10.8.0.3")]);
        Assert.Equal(2, Auto(doc).Count);

        Assert.True(VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]));
        Assert.Equal("PC-A", Auto(doc).Single().Name);
        Assert.Contains(mine, doc.Connections);
    }

    private static VpnDashboardDevice Dev(string name, string address, bool online, int minutesAgo = 0) =>
        new(name, address, online, online || minutesAgo > 0 ? Now.AddMinutes(-minutesAgo) : null);

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Due_pc_con_stesso_nome_e_senza_ip_non_rompono_gli_id()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2"), Dev("pc-a", "10.8.0.3"), Dev("PC-B", "10.8.0.4"), Dev("PC-C", "")]);
        Assert.Equal(3, Auto(doc).Count);
        Assert.False(doc.RepairConnectionIds());
    }

    [Fact]
    public void Stesso_nome_e_stesso_ip_dato_due_volte_tiene_una_connessione_con_l_id_stabile()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2/32", true), Dev("pc-a", "10.8.0.2", false, 5)]);
        var only = Auto(doc).Single();
        Assert.Equal(VpnFolder.StableId("pc-a"), only.Id);
        Assert.Equal("10.8.0.2", only.Host);
    }

    [Fact]
    public void Stesso_nome_e_ip_diversi_con_uno_solo_in_linea_tiene_quello_in_linea()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2", false, 90), Dev("PC-A", "10.8.0.3", true)]);
        var only = Auto(doc).Single();
        Assert.Equal("10.8.0.3", only.Host);
        Assert.Equal("PC-A", only.Name);
        Assert.Equal(VpnFolder.StableId("PC-A"), only.Id);
    }

    [Fact]
    public void Stesso_nome_e_ip_diversi_nessuno_in_linea_tiene_l_handshake_piu_recente()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2", false, 600), Dev("PC-A", "10.8.0.3", false, 60)]);
        Assert.Equal("10.8.0.3", Auto(doc).Single().Host);
    }

    [Fact]
    public void Stesso_nome_con_due_pc_in_linea_li_tiene_tutti_e_due_distinguendo_il_nome()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2", true), Dev("PC-A", "10.8.0.3", true)]);
        var auto = Auto(doc);
        Assert.Equal(2, auto.Count);
        Assert.Equal(["PC-A (10.8.0.2)", "PC-A (10.8.0.3)"], auto.Select(c => c.Name).Order().ToArray());
        Assert.Equal("10.8.0.3", auto.Single(c => c.Name == "PC-A (10.8.0.3)").Host);
        Assert.False(doc.RepairConnectionIds());

        // Al giro dopo, con l'elenco in un altro ordine, non cambia niente.
        Assert.False(VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.3", true), Dev("PC-A", "10.8.0.2", true)]));
    }

    [Fact]
    public void Con_due_pc_in_linea_quello_che_aveva_gia_la_connessione_tiene_il_nome_e_gli_accessi()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2", true)]);
        var existing = Auto(doc).Single();
        existing.Username = "mio";

        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.3", true), Dev("PC-A", "10.8.0.2", true)]);
        Assert.Same(existing, Auto(doc).Single(c => c.Name == "PC-A"));
        Assert.Equal("mio", existing.Username);
        Assert.Equal("PC-A (10.8.0.3)", Auto(doc).Single(c => c.Name != "PC-A").Name);
    }

    [Fact]
    public void Elenco_vuoto_transitorio_non_cancella_le_connessioni()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2", true), Dev("PC-A", "10.8.0.3", true)]);
        Assert.False(VpnFolder.Apply(doc, []));
        Assert.False(VpnFolder.Apply(doc, [Dev("", "10.8.0.9")]));
        Assert.Equal(2, Auto(doc).Count);
    }

    [Fact]
    public void Il_pc_stesso_non_compare_nella_cartella()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("MIO-PC", "10.8.0.2"), Dev("PC-B", "10.8.0.3")], "mio-pc");
        Assert.Equal("PC-B", Auto(doc).Single().Name);

        // Anche una connessione automatica che c'era già per sé stesso sparisce.
        var doc2 = new ConnectionsDocument();
        VpnFolder.Apply(doc2, [Dev("MIO-PC", "10.8.0.2")], "altro");
        Assert.Single(Auto(doc2));
        VpnFolder.Apply(doc2, [Dev("MIO-PC", "10.8.0.2"), Dev("PC-B", "10.8.0.3")], "MIO-PC");
        Assert.Equal("PC-B", Auto(doc2).Single().Name);
    }

    [Fact]
    public void Il_pc_stesso_si_riconosce_anche_dal_proprio_ip_vpn()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("QUALCOSA", "10.8.0.2/32"), Dev("QUALCOSA", "10.8.0.4")], "mio-pc", "10.8.0.2");
        Assert.Equal("10.8.0.4", Auto(doc).Single().Host);
    }

    [Fact]
    public void Nome_ripetuto_con_un_solo_pc_in_linea_non_sposta_la_connessione_esistente()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(Saved("10.8.0.2", "mario"));
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2", true)]);
        Assert.Equal("10.8.0.2", Auto(doc).Single().Host);

        // Compare un secondo PC con lo stesso nome ma spento: l'ordine dell'elenco non deve far saltare l'IP né i diritti.
        Assert.False(VpnFolder.Apply(doc, [Dev("pc-a", "10.8.0.9", false, 600), Dev("PC-A", "10.8.0.2", true)]));
        Assert.Equal("10.8.0.2", Auto(doc).Single().Host);
        Assert.False(VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2", true), Dev("pc-a", "10.8.0.9", false, 600)]));
        Assert.Equal("mario", Auto(doc).Single().Username);
    }

    [Fact]
    public void Connessione_che_punta_all_ip_di_un_pc_con_nome_ripetuto_e_ambigua()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(Saved("10.8.0.3", "mario", name: "pc-b"));
        VpnFolder.Apply(doc, [Dev("PC-B", "10.8.0.2"), Dev("PC-A", "10.8.0.3"), Dev("pc-a", "10.8.0.4")]);
        Assert.All(Auto(doc), c => Assert.Equal("", c.Username));
    }

    [Fact]
    public void Connessione_dietro_un_gateway_non_e_lo_stesso_pc()
    {
        var doc = new ConnectionsDocument();
        var viaGateway = Saved("10.8.0.2", "mario");
        viaGateway.UseGateway = true;
        viaGateway.GatewayHost = "gw.example.com";
        doc.Connections.Add(viaGateway);
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        Assert.Equal("", Auto(doc).Single().Username);
    }

    [Fact]
    public void Fra_due_gemelle_resta_quella_con_gli_accessi_dell_utente()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        var stable = Auto(doc).Single();
        var twin = stable.Clone();
        twin.Id = Guid.NewGuid();
        twin.Username = "mio";
        doc.Connections.Insert(doc.Connections.IndexOf(stable) + 1, twin);

        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        Assert.Same(twin, Auto(doc).Single());
    }

    [Fact]
    public void Doppioni_nati_dalla_sincronizzazione_vengono_ridotti_a_uno()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        var twin = Auto(doc).Single().Clone();
        twin.Id = Guid.NewGuid();
        doc.Connections.Add(twin);
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        Assert.Single(Auto(doc));
    }

    [Fact]
    public void Due_pc_che_creano_la_cartella_si_fondono_senza_doppioni_nella_sincronizzazione()
    {
        var a = new ConnectionsDocument();
        var b = new ConnectionsDocument();
        VpnFolder.Apply(a, [Dev("PC-X", "10.8.0.2")]);
        VpnFolder.Apply(b, [Dev("PC-X", "10.8.0.2")]);

        var fp = new SyncFingerprints(new byte[32]);
        SyncMerge.MergeInto(a, b, new Dictionary<string, string>(), fp);
        Assert.Single(a.Connections);
    }

    [Fact]
    public void Duplica_non_porta_il_segno_della_cartella_automatica()
    {
        var doc = new ConnectionsDocument();
        VpnFolder.Apply(doc, [Dev("PC-A", "10.8.0.2")]);
        Assert.Null(Auto(doc).Single().Duplicate().AutoVpnDevice);
    }
}
