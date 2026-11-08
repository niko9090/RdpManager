namespace RdpManager.Core;

/// <summary>Descrizioni in italiano dei codici di disconnessione più comuni del client RDP.</summary>
public static class DisconnectReasons
{
    private static readonly Dictionary<int, string> Known = new()
    {
        [1] = "Disconnessione locale",
        [2] = "Disconnessione remota dall'utente",
        [3] = "Disconnessione da parte del server",
        [260] = "Nome host non trovato (DNS)",
        [262] = "Memoria insufficiente",
        [263] = "Connessione annullata: certificato del server non accettato",
        [264] = "Tempo di connessione scaduto",
        [516] = "Impossibile stabilire la connessione (PC spento, firewall o porta errata?)",
        [520] = "Host non trovato",
        [772] = "Errore di rete in invio",
        [1030] = "Errore di sicurezza/crittografia",
        [1032] = "Errore interno del client",
        [1796] = "Tempo scaduto durante l'avvio della sessione",
        [2055] = "Credenziali non valide",
        [2056] = "Errore di licenza Desktop remoto",
        [2308] = "Connessione chiusa dal server o dalla rete",
        [2567] = "L'utente indicato non esiste",
        [2822] = "Errore di crittografia",
        [2823] = "Account disabilitato",
        [2825] = "Il server richiede l'autenticazione a livello di rete (NLA)",
        [7943] = "Mancano le credenziali e la richiesta interattiva è disattivata",
        [3079] = "Account con restrizioni di accesso",
        [3335] = "Account bloccato",
        [3591] = "Account scaduto",
        [3847] = "Password scaduta",
        [4615] = "La password deve essere cambiata prima del primo accesso",
    };

    public static bool IsUserInitiated(int reason) => reason is 1 or 2;

    public static string Describe(int reason) =>
        Known.TryGetValue(reason, out var text) ? text : $"Disconnessa (codice {reason})";
}
