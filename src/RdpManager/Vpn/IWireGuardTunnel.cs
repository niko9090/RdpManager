using RdpManager.Core;

namespace RdpManager.Vpn;

public enum TunnelState
{
    Spento,
    InAvvio,
    Acceso,
    InArresto,
    Errore,
}

/// <summary>Errore nell'accendere o spegnere il tunnel (messaggio già leggibile dall'utente).</summary>
public sealed class VpnException(string message) : Exception(message);

/// <summary>Il tunnel WireGuard di RdpManager.</summary>
public interface IWireGuardTunnel
{
    /// <summary>Stato attuale, riletto ogni volta.</summary>
    TunnelState Stato { get; }

    /// <summary>Accende il tunnel con la configurazione data (che deve essere valida). Se è già acceso o in avvio non fa nulla.</summary>
    /// <exception cref="ArgumentException">Configurazione non valida.</exception>
    /// <exception cref="VpnException">Accensione non riuscita o rifiutata (per esempio UAC annullato).</exception>
    void Accendi(WireGuardConfig config);

    /// <summary>Spegne il tunnel. Se è già spento non fa nulla.</summary>
    void Spegni();
}

/// <summary>Chi esegue un'azione con i diritti di amministratore (nella versione vera: RdpManager.exe con elevazione UAC).</summary>
public interface IVpnElevator
{
    /// <summary>Esegue RdpManager.exe con gli argomenti dati, elevato, e aspetta; restituisce il codice di uscita.</summary>
    int RunHelper(IReadOnlyList<string> args);
}

/// <summary>Lettura dello stato del servizio del tunnel (non richiede diritti di amministratore).</summary>
public interface IVpnServiceQuery
{
    TunnelState Query(string serviceName);
}

/// <summary>Gestione del servizio Windows del tunnel (richiede amministratore: la chiama solo l'assistente elevato).</summary>
public interface IServiceManager : IVpnServiceQuery
{
    bool Exists(string serviceName);
    void Create(string serviceName, string displayName, string binaryPathWithArgs);
    void Start(string serviceName);
    void Stop(string serviceName);
    void Delete(string serviceName);
}
