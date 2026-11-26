namespace RdpManager.Linux.Features;

/// <summary>Cosa significa un codice d'uscita di FreeRDP e cosa fare.</summary>
public sealed record ExitCodeInfo(string Meaning, string Hint);

/// <summary>I dati della finestra d'errore di una sessione: PC, codice, spiegazione e dettaglio tecnico di FreeRDP.</summary>
public sealed record SessionErrorInfo(string Title, string Host, int ExitCode, ExitCodeInfo Info, string Detail);

/// <summary>
/// I codici d'uscita di FreeRDP (sdl-freerdp, xfreerdp) spiegati in parole semplici, per la finestra d'errore.
/// Elenco dai codici FREERDP_ERROR_* / XF_EXIT_* di FreeRDP 2 e 3.
/// </summary>
public static class FreeRdpExitCodes
{
    private const string NotReachable = "Controlla che il PC remoto sia acceso e non in sospensione, che sia collegato alla VPN "
                                        + "(nella plancia VPN deve risultare in linea) e che il desktop remoto sia attivo. "
                                        + "Se il PC è in un'altra sede, il firewall di quella rete potrebbe bloccare la VPN.";

    /// <summary>Uscite che non sono errori (chiusura voluta dall'utente o dal PC remoto): niente finestra.</summary>
    public static bool IsNormalClose(int code) => code is 0 or 1 or 2 or 11 or 12 or 145;

    public static ExitCodeInfo Describe(int code) => code switch
    {
        1 => new("Il PC remoto ha chiuso la sessione.", "Riprova con «Riconnetti»."),
        2 => new("L'utente si è disconnesso dal PC remoto.", "Riprova con «Riconnetti»."),
        3 => new("Sessione chiusa per inattività.", "Riconnettiti; sul PC remoto si può allungare il tempo massimo di inattività."),
        4 => new("Il PC remoto ha chiuso la sessione perché l'accesso è durato troppo.", "Riprova e completa l'accesso più rapidamente."),
        5 => new("Qualcun altro si è collegato allo stesso PC con lo stesso utente e ha preso la sessione.", "Mettiti d'accordo con l'altro utente, poi riconnettiti."),
        6 => new("Il PC remoto ha finito la memoria.", "Chiudi qualche programma sul PC remoto o riavvialo."),
        7 => new("Il PC remoto ha rifiutato la connessione.", "Controlla che il desktop remoto sia attivo e che l'utente abbia il permesso di usarlo."),
        8 => new("Il PC remoto richiede una cifratura (FIPS) che questa connessione non usa.", "Va cambiata l'impostazione di sicurezza sul PC remoto."),
        9 => new("L'utente non ha i permessi per collegarsi al PC remoto.", "Aggiungi l'utente al gruppo «Utenti desktop remoto» sul PC remoto."),
        10 => new("Il PC remoto chiede di inserire di nuovo le credenziali.", "Riconnettiti e inserisci utente e password."),
        >= 16 and <= 31 => new("Problema con le licenze del desktop remoto sul PC remoto (codice di licenza).", "Riprova; se continua, serve controllare le licenze RDS del server."),
        >= 32 and <= 127 => new("Errore del protocollo del desktop remoto durante la sessione.", "Riprova con «Riconnetti»; se continua, prova a ridurre risoluzione o colori della connessione."),
        128 => new("FreeRDP non ha accettato le opzioni della connessione.", "Probabilmente la versione di FreeRDP installata è troppo vecchia: aggiornala."),
        129 => new("Memoria esaurita su questo computer.", "Chiudi qualche programma e riprova."),
        130 => new("Errore di protocollo durante la connessione.", "Riprova; se continua, controlla la sicurezza della connessione (NLA/TLS)."),
        131 or 141 or 147 => new("Il PC remoto non risponde sulla porta del desktop remoto (3389), oppure la connessione si è interrotta subito.", NotReachable),
        132 or 134 or 154 => new("Utente o password non accettati dal PC remoto.", "Controlla utente, dominio e password (attenzione al maiuscolo e al dominio, es. «AZIENDA\\utente»)."),
        133 => new("Il PC remoto e FreeRDP non si sono accordati sul tipo di sicurezza (NLA, TLS o RDP).", "Cambia la sicurezza nelle impostazioni della connessione, oppure controlla quella richiesta dal PC remoto."),
        135 => new("L'account è bloccato per troppi tentativi sbagliati.", "Aspetta lo sblocco automatico o chiedi all'amministratore di sbloccarlo."),
        136 or 137 or 138 => new("La connessione non è riuscita a partire.", "Riprova; se continua, il dettaglio tecnico qui sotto dice cosa è andato storto."),
        139 or 140 => new("Il nome del PC remoto non si trova.", "Usa l'indirizzo IP del PC al posto del nome (per i PC della VPN: 10.66.0.x)."),
        142 => new("Il PC remoto ha rifiutato l'avvio della sessione.", "Riprova; se continua, riavvia il servizio desktop remoto sul PC remoto."),
        143 => new("Problema con il certificato o con la cifratura (TLS) del PC remoto.", "Riprova; se il certificato è cambiato per un motivo legittimo, accettalo quando viene chiesto."),
        144 => new("Permessi insufficienti per questa connessione.", "Usa un utente con i permessi di accesso remoto."),
        148 or 152 => new("La password dell'utente è scaduta.", "Cambia la password sul PC remoto (o sul dominio), poi riprova con quella nuova."),
        149 => new("Il PC remoto chiede di cambiare la password prima di entrare.", "Cambia la password da un PC Windows o direttamente sul PC remoto, poi riprova."),
        150 => new("Il server di autenticazione del dominio (Kerberos) non si raggiunge.", "Controlla la rete verso il dominio, oppure entra con un utente locale del PC."),
        151 => new("L'account è disattivato.", "Chiedi all'amministratore di riattivarlo."),
        153 => new("Il certificato di questo computer è stato revocato.", "Serve l'amministratore."),
        155 => new("Accesso negato dal PC remoto.", "Controlla che l'utente sia nel gruppo «Utenti desktop remoto» del PC remoto."),
        156 => new("L'account ha restrizioni (orari o PC da cui può entrare).", "Chiedi all'amministratore di controllare le restrizioni dell'account."),
        157 => new("L'account è scaduto.", "Chiedi all'amministratore di rinnovarlo."),
        158 => new("L'utente non ha il permesso di accedere con il desktop remoto.", "Aggiungi l'utente al gruppo «Utenti desktop remoto» sul PC remoto."),
        159 => new("Mancano utente e password per questo PC.", "Inserisci le credenziali quando vengono chieste, o salvale nella connessione."),
        255 => new("Errore sconosciuto di FreeRDP.", "Riprova; il dettaglio tecnico qui sotto può aiutare a capire."),
        _ => new("La connessione si è chiusa con un errore.", "Riprova; il dettaglio tecnico qui sotto può aiutare a capire."),
    };
}
