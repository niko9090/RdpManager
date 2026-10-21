namespace RdpManager.Core;

/// <summary>Tipo di problema, per raggruppare i messaggi e scegliere cosa proporre.</summary>
public enum ConnectionErrorKind
{
    /// <summary>Chiusa dall'utente (qui o sul PC remoto): non è un errore.</summary>
    UserInitiated,
    /// <summary>Nome del PC non risolto o PC non raggiungibile.</summary>
    Network,
    /// <summary>Utente, password o account.</summary>
    Credentials,
    /// <summary>Certificato o crittografia.</summary>
    Security,
    /// <summary>Il server ha chiuso o rifiutato la sessione.</summary>
    Server,
    /// <summary>Licenze, memoria, errori interni del client.</summary>
    Client,
    Unknown,
}

/// <summary>Spiegazione di una disconnessione: cosa è successo, causa probabile, cosa fare.</summary>
/// <param name="Reason">Codice di disconnessione del controllo RDP.</param>
public sealed record ConnectionErrorInfo(int Reason, ConnectionErrorKind Kind, string Title, string Cause, string Solution)
{
    /// <summary>Testo su più righe per la scheda della sessione.</summary>
    public string ToMessage() =>
        Kind == ConnectionErrorKind.UserInitiated
            ? Title
            : $"{Title}\nCausa probabile: {Cause}\nCosa fare: {Solution}";
}

/// <summary>
/// Mappa dei codici di disconnessione del client RDP (e dei codici estesi del server) verso messaggi in italiano
/// con causa probabile e soluzione. Le descrizioni brevi restano in <see cref="DisconnectReasons"/>.
/// </summary>
public static class ConnectionErrors
{
    private sealed record Entry(ConnectionErrorKind Kind, string Cause, string Solution);

    private const string CheckNetwork =
        "Controlla che il PC remoto sia acceso e in rete («Verifica raggiungibilità» nel menu), che il nome o l'indirizzo sia giusto e che Desktop remoto sia attivo sul PC remoto.";

    private static readonly Dictionary<int, Entry> Known = new()
    {
        [1] = new(ConnectionErrorKind.UserInitiated, "La sessione è stata chiusa da questo PC.", "Nessuna: «Riconnetti» (F5) la riapre."),
        [2] = new(ConnectionErrorKind.UserInitiated, "La sessione è stata chiusa sul PC remoto (disconnessione o uscita).", "Nessuna: «Riconnetti» (F5) la riapre."),
        [3] = new(ConnectionErrorKind.Server, "Il PC remoto ha chiuso la sessione: un amministratore, un altro accesso con lo stesso utente o un tempo di inattività.", "Riconnetti; se succede spesso, controlla i limiti di tempo delle sessioni sul PC remoto."),
        [260] = new(ConnectionErrorKind.Network, "Il nome del PC non viene trovato (DNS).", "Controlla il nome scritto nella connessione, prova con l'indirizzo IP o verifica di essere nella rete giusta (VPN)."),
        [262] = new(ConnectionErrorKind.Client, "Memoria insufficiente su questo PC.", "Chiudi qualche sessione o programma e riprova."),
        [263] = new(ConnectionErrorKind.Security, "Il certificato del PC remoto non è stato accettato.", "Riconnetti e, se il nome del PC è quello atteso, rispondi «Sì» all'avviso sul certificato."),
        [264] = new(ConnectionErrorKind.Network, "Il PC remoto non ha risposto in tempo.", CheckNetwork),
        [516] = new(ConnectionErrorKind.Network, "Non è stato possibile raggiungere il PC remoto: spento, firewall o porta sbagliata.", CheckNetwork + " La porta predefinita è 3389."),
        [520] = new(ConnectionErrorKind.Network, "Il PC remoto non è stato trovato.", CheckNetwork),
        [772] = new(ConnectionErrorKind.Network, "Errore di rete durante l'invio dei dati.", "Controlla la connessione di rete e riconnetti."),
        [1030] = new(ConnectionErrorKind.Security, "Errore di sicurezza o di crittografia nella negoziazione.", "Riconnetti; se persiste, controlla data e ora dei due PC e gli aggiornamenti di Windows sul PC remoto."),
        [1032] = new(ConnectionErrorKind.Client, "Errore interno del client Desktop remoto.", "Riconnetti; se persiste usa «Apri con mstsc»."),
        [1796] = new(ConnectionErrorKind.Network, "Tempo scaduto durante l'avvio della sessione.", "Riconnetti; se la rete è lenta riprova tra poco."),
        [2055] = new(ConnectionErrorKind.Credentials, "Utente o password non validi.", "Correggi utente, dominio e password nella connessione (o nel profilo di credenziali) e riconnetti."),
        [2056] = new(ConnectionErrorKind.Server, "Il server non ha licenze Desktop remoto disponibili.", "Chiedi all'amministratore del server di controllare le licenze RDS."),
        [2308] = new(ConnectionErrorKind.Network, "La connessione è stata interrotta dal PC remoto o dalla rete.", "Riconnetti; la riconnessione automatica (nella modifica della connessione) lo fa da sola."),
        [2567] = new(ConnectionErrorKind.Credentials, "L'utente indicato non esiste sul PC remoto o nel dominio.", "Controlla il nome utente e il dominio (es. DOMINIO\\utente o utente@dominio)."),
        [2822] = new(ConnectionErrorKind.Security, "Errore di crittografia.", "Riconnetti; se persiste, controlla gli aggiornamenti di Windows sui due PC."),
        [2823] = new(ConnectionErrorKind.Credentials, "L'account è disabilitato.", "Chiedi all'amministratore di riattivare l'account."),
        [2825] = new(ConnectionErrorKind.Credentials, "Il PC remoto richiede l'autenticazione a livello di rete (NLA) e servono credenziali.", "Indica utente e password nella connessione, oppure lascia vuota la password e inseriscila quando viene chiesta."),
        [3079] = new(ConnectionErrorKind.Credentials, "L'account ha restrizioni di accesso (orari, PC consentiti o password vuota).", "Chiedi all'amministratore di controllare le restrizioni dell'account."),
        [3335] = new(ConnectionErrorKind.Credentials, "L'account è bloccato per troppi tentativi con la password sbagliata.", "Attendi lo sblocco o chiedi all'amministratore; poi correggi la password salvata."),
        [3591] = new(ConnectionErrorKind.Credentials, "L'account è scaduto.", "Chiedi all'amministratore di rinnovare l'account."),
        [3847] = new(ConnectionErrorKind.Credentials, "La password è scaduta.", "Cambia la password (per esempio con «Apri con mstsc» o direttamente sul PC remoto) e aggiornala nella connessione."),
        [4615] = new(ConnectionErrorKind.Credentials, "La password va cambiata prima del primo accesso.", "Accedi una volta con «Apri con mstsc» per cambiarla, poi aggiornala nella connessione."),
        [7943] = new(ConnectionErrorKind.Credentials, "Mancano le credenziali e la loro richiesta è disattivata.", "Indica utente e password nella connessione (o in un profilo)."),
    };

    /// <summary>Codici estesi (ExtendedDisconnectReason) che spiegano meglio una chiusura da parte del server.</summary>
    private static readonly Dictionary<int, Entry> Extended = new()
    {
        [2] = new(ConnectionErrorKind.Server, "Sul PC remoto è stata chiusa la sessione (disconnessione o uscita dell'utente).", "Riconnetti quando serve."),
        [3] = new(ConnectionErrorKind.Server, "La sessione è rimasta inattiva troppo a lungo e il PC remoto l'ha chiusa.", "Riconnetti; il limite di inattività si cambia nei criteri di Desktop remoto del PC remoto."),
        [4] = new(ConnectionErrorKind.Server, "L'accesso non è stato completato in tempo.", "Riconnetti e inserisci le credenziali più rapidamente, o salvale nella connessione."),
        [5] = new(ConnectionErrorKind.Server, "Un altro accesso con lo stesso utente ha preso il posto di questa sessione.", "Controlla chi sta usando lo stesso utente; riconnetti per riprendere la sessione."),
        [6] = new(ConnectionErrorKind.Server, "Il PC remoto ha esaurito la memoria.", "Chiudi qualche programma sul PC remoto o riavvialo."),
        [7] = new(ConnectionErrorKind.Server, "Il PC remoto ha rifiutato la connessione.", "Controlla che l'utente possa accedere con Desktop remoto (gruppo «Utenti desktop remoto»)."),
        [9] = new(ConnectionErrorKind.Credentials, "L'utente non ha il permesso di accedere con Desktop remoto.", "Aggiungi l'utente al gruppo «Utenti desktop remoto» sul PC remoto."),
        [11] = new(ConnectionErrorKind.Credentials, "Il PC remoto richiede credenziali inserite di nuovo.", "Riconnetti e inserisci la password quando viene chiesta."),
        [12] = new(ConnectionErrorKind.Server, "La sessione è stata chiusa da un amministratore sul PC remoto.", "Riconnetti o chiedi all'amministratore."),
        [13] = new(ConnectionErrorKind.UserInitiated, "L'utente è uscito (disconnessione) dal PC remoto.", "Nessuna: «Riconnetti» apre una sessione nuova."),
    };

    /// <summary>Codici di rete per cui, con un gateway, la causa può essere il gateway stesso.</summary>
    private static readonly HashSet<int> GatewaySensitive = [260, 264, 516, 520, 772, 1796, 2308];

    /// <param name="reason">Codice di disconnessione (evento OnDisconnected).</param>
    /// <param name="extended">Codice esteso del server, se disponibile (0 o null = nessuno).</param>
    /// <param name="usesGateway">La connessione passa da un Remote Desktop Gateway.</param>
    public static ConnectionErrorInfo Explain(int reason, int? extended = null, bool usesGateway = false)
    {
        var title = DisconnectReasons.Describe(reason);
        Entry entry;
        // Il codice esteso è più preciso quando il server ha chiuso o rifiutato (o il client non sa dire di più).
        if (extended is int ext && ext > 0 && Extended.TryGetValue(ext, out var byServer)
            && (reason is 2 or 3 or 2308 || !Known.ContainsKey(reason)))
            entry = byServer;
        else if (Known.TryGetValue(reason, out var known))
            entry = known;
        else
            entry = new(ConnectionErrorKind.Unknown, $"Disconnessione con un codice non riconosciuto ({reason}).",
                "Riconnetti; se succede di nuovo prova «Apri con mstsc», che può mostrare un messaggio più preciso.");

        var solution = entry.Solution;
        if (usesGateway && GatewaySensitive.Contains(reason))
            solution += " La connessione passa da un Remote Desktop Gateway: controlla anche il suo indirizzo e le sue credenziali.";
        return new ConnectionErrorInfo(reason, entry.Kind, title, entry.Cause, solution);
    }

    /// <summary>Vero se rifare la connessione senza cambiare nulla non può riuscire (credenziali, account, certificato rifiutato).</summary>
    public static bool NeedsUserAction(int reason) =>
        Known.TryGetValue(reason, out var e) && e.Kind is ConnectionErrorKind.Credentials || reason == 263;
}
