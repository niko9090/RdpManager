using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Profili di credenziali: risoluzione, cifratura in connections.json ed esportazione .rdpm.</summary>
public sealed class CredentialProfileTests : IDisposable
{
    private const string FilePassword = "password-del-file-123";
    private const int FastIterations = 1000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerProfiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static (ConnectionsDocument Doc, CredentialProfile Profile, RdpConnection WithProfile, RdpConnection Own) Sample()
    {
        var profile = new CredentialProfile { Name = "Amministratore", Username = "admin", Domain = "UFFICIO", Password = "Segreta-Profilo-1!" };
        var withProfile = new RdpConnection { Name = "Server", Host = "srv.example.test", Username = "vecchio", CredentialProfileId = profile.Id };
        var own = new RdpConnection { Name = "PC", Host = "pc.example.test", Username = "mario", Domain = "CASA", Password = "Propria-2!" };
        var doc = new ConnectionsDocument { Connections = [withProfile, own], CredentialProfiles = [profile] };
        return (doc, profile, withProfile, own);
    }

    [Fact]
    public void Resolve_UsesProfileWhenSet_OtherwiseOwnCredentials()
    {
        var (doc, profile, withProfile, own) = Sample();

        var r = doc.ResolveCredentials(withProfile);
        Assert.Equal(("admin", "UFFICIO", "Segreta-Profilo-1!", (Guid?)profile.Id), (r.Username, r.Domain, r.Password, r.ProfileId));

        var o = doc.ResolveCredentials(own);
        Assert.Equal(("mario", "CASA", "Propria-2!", (Guid?)null), (o.Username, o.Domain, o.Password, o.ProfileId));
    }

    [Fact]
    public void Resolve_MissingProfile_FallsBackToConnection()
    {
        var c = new RdpConnection { Host = "x", Username = "proprio", CredentialProfileId = Guid.NewGuid() };
        var r = new ConnectionsDocument { Connections = [c] }.ResolveCredentials(c);
        Assert.Equal("proprio", r.Username);
        Assert.Null(r.ProfileId);
    }

    [Fact]
    public void WithResolvedCredentials_CopiesProfileIntoAClone()
    {
        var (doc, _, withProfile, _) = Sample();
        var resolved = doc.WithResolvedCredentials(withProfile);
        Assert.NotSame(withProfile, resolved);
        Assert.Equal("admin", resolved.Username);
        Assert.Equal("UFFICIO", resolved.Domain);
        Assert.Equal("Segreta-Profilo-1!", resolved.Password);
        Assert.Equal("vecchio", withProfile.Username); // l'originale non cambia
    }

    [Fact]
    public void GatewayCredentials_SameAsConnectionOrProfile()
    {
        var (doc, profile, withProfile, own) = Sample();
        Assert.Null(doc.ResolveGatewayCredentials(own)); // senza gateway

        own.UseGateway = true;
        own.GatewayHost = "gw.example.test";
        Assert.Equal("mario", doc.ResolveGatewayCredentials(own)!.Username);

        own.GatewayCredentials = GatewayCredentialsSource.Profile;
        own.GatewayProfileId = profile.Id;
        Assert.Equal("admin", doc.ResolveGatewayCredentials(own)!.Username);

        own.GatewayProfileId = Guid.NewGuid(); // profilo sparito → le chiederà il controllo
        Assert.Null(doc.ResolveGatewayCredentials(own));

        withProfile.UseGateway = true;
        withProfile.GatewayHost = ""; // senza host il gateway non è attivo
        Assert.Null(doc.ResolveGatewayCredentials(withProfile));
    }

    [Fact]
    public void DeleteProfile_ConnectionsKeepUserAndLoseReference()
    {
        var (doc, profile, withProfile, own) = Sample();
        withProfile.Username = "";
        own.UseGateway = true;
        own.GatewayHost = "gw";
        own.GatewayCredentials = GatewayCredentialsSource.Profile;
        own.GatewayProfileId = profile.Id;

        Assert.Equal([withProfile, own], doc.ConnectionsUsingProfile(profile.Id));
        Assert.True(doc.DeleteProfile(profile.Id));

        Assert.Empty(doc.CredentialProfiles);
        Assert.Null(withProfile.CredentialProfileId);
        Assert.Equal("admin", withProfile.Username);
        Assert.Equal("UFFICIO", withProfile.Domain);
        Assert.Null(own.GatewayProfileId);
        Assert.Equal(GatewayCredentialsSource.SameAsConnection, own.GatewayCredentials);
        Assert.False(doc.DeleteProfile(profile.Id));
    }

    [Fact]
    public void Store_ProfilePasswordIsDpapiEncrypted_AndRoundTrips()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "connections.json");
        var (doc, profile, withProfile, _) = Sample();
        new ConnectionStore(path).Save(doc);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("Segreta-Profilo-1!", json);
        Assert.DoesNotContain("Propria-2!", json);
        Assert.Contains("credentialProfiles", json);

        var loaded = new ConnectionStore(path).Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, loaded.Version);
        var p = Assert.Single(loaded.CredentialProfiles);
        Assert.Equal(profile.Id, p.Id);
        Assert.Equal("Amministratore", p.Name);
        Assert.Equal("Segreta-Profilo-1!", p.Password);
        Assert.Null(p.ProtectedPassword);
        Assert.Equal(profile.Id, loaded.Find(withProfile.Id)!.CredentialProfileId);
    }

    [Fact]
    public void Rdpm_WithFilePassword_RoundTripsProfiles_WithoutPlainTextPasswords()
    {
        var (doc, profile, withProfile, _) = Sample();
        var json = RdpmFile.Export(doc, FilePassword, FastIterations);

        Assert.DoesNotContain("Segreta-Profilo-1!", json);
        Assert.DoesNotContain("Propria-2!", json);

        var imported = RdpmFile.Import(json, FilePassword);
        var p = Assert.Single(imported.CredentialProfiles);
        Assert.Equal(profile.Id, p.Id);
        Assert.Equal("admin", p.Username);
        Assert.Equal("Segreta-Profilo-1!", p.Password);
        Assert.Equal(profile.Id, imported.Connections.Single(c => c.Id == withProfile.Id).CredentialProfileId);
    }

    [Fact]
    public void Rdpm_WithoutFilePassword_ExcludesProfilePasswords()
    {
        var (doc, _, _, _) = Sample();
        var json = RdpmFile.Export(doc);
        Assert.False(RdpmFile.IsEncrypted(json));
        Assert.DoesNotContain("Segreta-Profilo-1!", json);

        var p = Assert.Single(RdpmFile.Import(json).CredentialProfiles);
        Assert.Equal("admin", p.Username);
        Assert.Null(p.Password);
        Assert.False(p.HasPassword);
    }

    [Fact]
    public void Rdpm_WrongFilePassword_ImportsNothing()
    {
        var (doc, _, _, _) = Sample();
        var json = RdpmFile.Export(doc, FilePassword, FastIterations);
        Assert.Throws<RdpmPasswordException>(() => RdpmFile.Import(json, "sbagliata"));
    }

    [Fact]
    public void Rdpm_ProfilePasswordCannotBeMovedToAConnection()
    {
        // Il contesto autenticato di AES-GCM lega ogni password al suo profilo: spostarla altrove la rende illeggibile.
        var (doc, profile, _, own) = Sample();
        var node = System.Text.Json.Nodes.JsonNode.Parse(RdpmFile.Export(doc, FilePassword, FastIterations))!;
        var sealedProfile = node["credentialProfiles"]![0]!["encryptedPassword"]!.GetValue<string>();
        foreach (var entry in node["connections"]!.AsArray())
            if (entry!["connection"]!["id"]!.GetValue<string>() == own.Id.ToString())
                entry["encryptedPassword"] = sealedProfile;
        Assert.Throws<InvalidDataException>(() => RdpmFile.Import(node.ToJsonString(), FilePassword));
        Assert.NotEqual(Guid.Empty, profile.Id);
    }

    [Fact]
    public void Merge_ProfilesWithoutDuplicates_AndReferencesFollowTheExistingProfile()
    {
        var (source, profile, withProfile, _) = Sample();
        var imported = RdpmFile.Import(RdpmFile.Export(source, FilePassword, FastIterations), FilePassword);

        // Sul PC di destinazione esiste già lo stesso profilo (stesso nome/utente/dominio) con un altro Id e senza password.
        var existing = new CredentialProfile { Name = "amministratore", Username = "ADMIN", Domain = "ufficio" };
        var target = new ConnectionsDocument { CredentialProfiles = [existing] };

        var result = RdpmFile.ApplyImport(target, imported, ImportMode.Merge);
        Assert.Equal(0, result.ProfilesAdded);
        Assert.Equal(2, result.Added);
        Assert.Single(target.CredentialProfiles);
        Assert.Equal("Segreta-Profilo-1!", existing.Password); // presa dal file perché mancava
        Assert.Equal(existing.Id, target.Find(withProfile.Id)!.CredentialProfileId);

        // Reimportando lo stesso file non si crea nulla di nuovo.
        var again = RdpmFile.ApplyImport(target, RdpmFile.Import(RdpmFile.Export(source)), ImportMode.Merge);
        Assert.Equal(new ImportResult(0, 2, 0, 0, 0), again);
        Assert.Single(target.CredentialProfiles);
        Assert.NotEqual(existing.Id, profile.Id);
    }

    [Fact]
    public void Merge_NewProfileIsAdded_AndUnknownReferencesAreDropped()
    {
        var (source, profile, withProfile, own) = Sample();
        own.CredentialProfileId = Guid.NewGuid(); // profilo che non è nel file e non esiste nella destinazione
        var target = new ConnectionsDocument();

        var result = RdpmFile.ApplyImport(target, RdpmFile.Import(RdpmFile.Export(source)), ImportMode.Merge);
        Assert.Equal(1, result.ProfilesAdded);
        Assert.Equal(profile.Id, target.Find(withProfile.Id)!.CredentialProfileId);
        Assert.Null(target.Find(own.Id)!.CredentialProfileId);
    }

    [Fact]
    public void Replace_KeepsExistingProfiles_AndAddsThoseOfTheFile()
    {
        // In sostituzione cambiano connessioni e cartelle; i profili (con le loro password DPAPI) non si perdono.
        var (source, profile, _, _) = Sample();
        var old = new CredentialProfile { Name = "Vecchio", Username = "vecchio", Password = "Da-Non-Perdere" };
        var target = new ConnectionsDocument
        {
            Connections = [new RdpConnection { Host = "da-sostituire" }],
            CredentialProfiles = [old],
        };
        var result = RdpmFile.ApplyImport(target, RdpmFile.Import(RdpmFile.Export(source)), ImportMode.Replace);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.ProfilesAdded);
        Assert.Equal([old.Id, profile.Id], target.CredentialProfiles.Select(p => p.Id));
        Assert.Equal("Da-Non-Perdere", old.Password);
        Assert.DoesNotContain(target.Connections, c => c.Host == "da-sostituire");
    }

    [Fact]
    public void DisplayName_ShowsAccount()
    {
        Assert.Equal("Admin (UFFICIO\\admin)", new CredentialProfile { Name = "Admin", Username = "admin", Domain = "UFFICIO" }.DisplayName);
        Assert.Equal("admin", new CredentialProfile { Username = "admin" }.DisplayName);
    }
}
