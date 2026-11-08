using RdpManager.Core;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Sessions;

/// <summary>
/// Apre le sessioni con FreeRDP. Se FreeRDP non è installato <see cref="IsAvailable"/> è falso e la scheda mostra
/// il comando per installarlo. Se la connessione usa la VPN WireGuard (il PC sta nella rete della VPN e questo PC
/// ha un profilo) la VPN si accende prima di avviare FreeRDP.
/// </summary>
public sealed class FreeRdpSessionHost : ISessionHost
{
    private readonly Func<string, bool> _isInstalled;
    private readonly Func<RdpConnection, ResolvedCredentials?> _gatewayCredentials;
    private readonly Func<VpnProfile?> _vpnProfile;
    private readonly Func<LinuxWireGuard> _vpn;
    private readonly Action<Action> _post;
    private readonly List<FreeRdpSession> _sessions = [];

    /// <param name="isInstalled">Se un programma è installato (di norma <see cref="FreeRdpLocator.IsOnPath"/>).</param>
    /// <param name="gatewayCredentials">Credenziali del gateway per la connessione (<see cref="ConnectionsDocument.ResolveGatewayCredentials"/>).</param>
    /// <param name="post">Esegue un'azione sul thread dell'interfaccia.</param>
    public FreeRdpSessionHost(
        Func<string, bool>? isInstalled = null,
        Func<RdpConnection, ResolvedCredentials?>? gatewayCredentials = null,
        Func<VpnProfile?>? vpnProfile = null,
        Func<LinuxWireGuard>? vpn = null,
        Action<Action>? post = null)
    {
        _isInstalled = isInstalled ?? (name => FreeRdpLocator.IsOnPath(name));
        _gatewayCredentials = gatewayCredentials ?? (_ => null);
        _vpnProfile = vpnProfile ?? (() => VpnProfile.Load());
        _vpn = vpn ?? (() => LinuxPlatformInitHelper.CreateVpn());
        _post = post ?? (a => a());
    }

    public bool IsAvailable => FreeRdpLocator.Find(false, _isInstalled) is not null;

    public string? UnavailableReason => IsAvailable ? null : FreeRdpLocator.NotInstalledMessage();

    public ISession Open(RdpConnection connection)
    {
        var client = FreeRdpLocator.Find(true, _isInstalled);
        if (client is null)
            return new MissingFreeRdpSession(connection, FreeRdpLocator.NotInstalledMessage());

        var session = new FreeRdpSession(connection, client, _gatewayCredentials(connection), ct => PrepareVpnAsync(connection, ct), _post);
        lock (_sessions)
            _sessions.Add(session);
        session.Closed += (_, _) =>
        {
            lock (_sessions)
                _sessions.Remove(session);
        };
        return session;
    }

    /// <summary>Null se va tutto bene (VPN non necessaria o accesa), altrimenti il motivo.</summary>
    private async Task<string?> PrepareVpnAsync(RdpConnection connection, CancellationToken ct)
    {
        if (!FreeRdpCommandBuilder.HostIsOnVpnNetwork(connection.Host))
            return null;
        VpnProfile? profile;
        try { profile = _vpnProfile(); }
        catch (Exception ex) { return "Profilo VPN non leggibile: " + ex.Message; }
        if (profile is null || !profile.HasKey)
            return null; // la VPN non è configurata su questo PC: si prova comunque (potrebbe essere già raggiungibile)
        try
        {
            var vpn = _vpn();
            if (await vpn.IsConnectedAsync(ct).ConfigureAwait(false))
                return null;
            var result = await vpn.ConnectAsync(profile.ToConfig(), ct).ConfigureAwait(false);
            return result.Success ? null : "Non riesco ad accendere la VPN: " + result.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return "Non riesco ad accendere la VPN: " + ex.Message;
        }
    }

    /// <summary>Chiude tutte le sessioni aperte e aspetta (al massimo <paramref name="wait"/>) che i processi finiscano: all'uscita dal programma.</summary>
    public void ShutdownAll(TimeSpan wait)
    {
        FreeRdpSession[] open;
        lock (_sessions)
            open = [.. _sessions];
        foreach (var s in open)
            s.Close();
        Task.WaitAll(open.Select(s => s.StopTask).ToArray(), wait);
    }
}

internal static class LinuxPlatformInitHelper
{
    public static LinuxWireGuard CreateVpn() => global::RdpManager.Platform.Linux.LinuxPlatformInit.CreateVpn();
}

/// <summary>Scheda aperta senza FreeRDP: spiega come installarlo.</summary>
public sealed class MissingFreeRdpSession : ISession
{
    public MissingFreeRdpSession(RdpConnection connection, string message)
    {
        ConnectionId = connection.Id;
        Title = connection.DisplayName;
        Content = new PlaceholderSessionContent(connection.DisplayName, $"{connection.Host}:{connection.Port}", message);
    }

    public Guid ConnectionId { get; }
    public string Title { get; }
    public object Content { get; }
    public event EventHandler? Closed;
    public void Close() => Closed?.Invoke(this, EventArgs.Empty);
}
