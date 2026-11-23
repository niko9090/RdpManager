using RdpManager.Core;

namespace RdpManager.Platform.Linux;

/// <summary>
/// Connessione automatica della VPN: chiamata all'avvio e poi a intervalli, accende il tunnel se il profilo è completo e
/// l'opzione è attiva, e lo riaccende se cade. Non insiste: dopo <see cref="MaxFailures"/> tentativi falliti di fila smette
/// (ogni tentativo con wg-quick chiede la password) finché la VPN non risulta di nuovo accesa o si chiama <see cref="Reset"/>.
/// </summary>
public sealed class LinuxVpnAutoConnector(LinuxWireGuard vpn, Func<VpnProfile> loadProfile, Func<bool>? toolsPresent = null)
{
    public const int MaxFailures = 3;

    private readonly Func<bool> _toolsPresent = toolsPresent ?? (() => LinuxWireGuardInstall.ToolsPresent(LinuxTools.Exists));
    private int _failures;
    private bool _wasConnected;
    private bool _warnedMissingTools;

    public int Failures => _failures;

    public void Reset()
    {
        _failures = 0;
        _warnedMissingTools = false;
    }

    /// <summary>Un giro di controllo. Restituisce una riga da mostrare ("" = niente da dire).</summary>
    public async Task<string> TickAsync(CancellationToken ct = default)
    {
        var profile = loadProfile();
        if (!VpnProvisioning.ShouldAutoConnect(profile) || vpn.StoppedByUser || _failures >= MaxFailures)
            return "";

        if (await vpn.GetBackendAsync(ct).ConfigureAwait(false) == LinuxVpnBackend.WgQuick && !_toolsPresent())
        {
            if (_warnedMissingTools)
                return "";
            _warnedMissingTools = true;
            return "WireGuard non è installato: apri VPN e premi «Installa WireGuard» per collegarti da solo.";
        }
        _warnedMissingTools = false;

        if (await vpn.IsConnectedAsync(ct).ConfigureAwait(false))
        {
            _failures = 0;
            _wasConnected = true;
            return "";
        }

        var dropped = _wasConnected;
        _wasConnected = false;
        var result = await vpn.EnsureConnectedAsync(profile.ToConfig(), ct).ConfigureAwait(false);
        if (result.Success)
        {
            _failures = 0;
            _wasConnected = true;
            return dropped ? "VPN ricollegata." : "VPN collegata.";
        }
        _failures++;
        return "VPN non collegata: " + result.Message;
    }
}
