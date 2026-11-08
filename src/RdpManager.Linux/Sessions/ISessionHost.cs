using RdpManager.Core;

namespace RdpManager.Linux.Sessions;

/// <summary>
/// Chi apre le sessioni remote nelle schede. L'implementazione FreeRDP si sostituisce al segnaposto
/// (<see cref="PlaceholderSessionHost"/>) senza toccare finestra e viewmodel.
/// </summary>
public interface ISessionHost
{
    /// <summary>Vero se le sessioni si possono davvero aprire.</summary>
    bool IsAvailable { get; }

    /// <summary>Spiegazione mostrata all'utente quando <see cref="IsAvailable"/> è falso.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Apre una sessione per la connessione, che ha già utente, dominio e password effettivi
    /// (<see cref="ConnectionsDocument.WithResolvedCredentials"/>).
    /// </summary>
    ISession Open(RdpConnection connection);
}

/// <summary>Una sessione aperta in una scheda.</summary>
public interface ISession
{
    Guid ConnectionId { get; }

    string Title { get; }

    /// <summary>
    /// Contenuto della scheda: un viewmodel o un controllo. L'interfaccia lo associa al modello di visualizzazione
    /// (per il segnaposto <see cref="PlaceholderSessionContent"/>).
    /// </summary>
    object Content { get; }

    /// <summary>Chiude la sessione e libera le risorse.</summary>
    void Close();

    /// <summary>La sessione è terminata (dall'host remoto o da <see cref="Close"/>).</summary>
    event EventHandler? Closed;
}
