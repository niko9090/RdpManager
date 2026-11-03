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

    /// <summary>Ultimo stato scritto dal servizio (handshake, byte); null se spento o non ancora disponibile.</summary>
    VpnStatus? Dettagli => null;
}

/// <summary>Chi esegue un'azione con i diritti di amministratore (nella versione vera: RdpManager.exe con elevazione UAC).</summary>
public interface IVpnElevator
{
    /// <summary>Esegue RdpManager.exe con gli argomenti dati, elevato, e aspetta; restituisce il codice di uscita.</summary>
    int RunHelper(IReadOnlyList<string> args);
}

/// <summary>Controllo del servizio del tunnel senza diritti di amministratore (il servizio ne dà i permessi agli utenti).</summary>
public interface IVpnServiceControl
{
    TunnelState Query(string serviceName);
    bool Exists(string serviceName);
    /// <exception cref="InvalidOperationException">Il gestore dei servizi rifiuta (per esempio permessi mancanti).</exception>
    void Start(string serviceName);
    /// <exception cref="InvalidOperationException">Il gestore dei servizi rifiuta.</exception>
    void Stop(string serviceName);
}

/// <summary>Gestione del servizio Windows del tunnel (creare e cancellare richiede amministratore: la chiama solo l'assistente elevato).</summary>
public interface IServiceManager : IVpnServiceControl
{
    void Create(string serviceName, string displayName, string binaryPathWithArgs);
    /// <summary>Permette agli utenti locali di avviare e fermare il servizio (e leggerne lo stato) senza UAC.</summary>
    void GrantUserControl(string serviceName);
    void Delete(string serviceName);
}
