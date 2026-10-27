# Prove di aggiornamento 1.6.1 (27/10/2026)

Fatte sull'installazione per utente di un PC di sviluppo (Windows 11), con una copia dei dati e della cartella
installata fatta prima. Due connessioni di prova verso un PC con il Desktop remoto acceso; le due sessioni erano
aperte in schede e ferme sulla richiesta della password (le password le inserisce solo il proprietario del PC).
`connections.json` contiene anche le impostazioni (sessioni da riaprire comprese): l'impronta SHA256 copre entrambe.

| Prova | Come | Esito |
|---|---|---|
| A. 1.6.0 → 1.6.1 con 2 sessioni aperte | 1.6.0 installata da `RdpManager-Setup-1.6.0.exe`, app aperta con le 2 schede; l'installer 1.6.1 avviato come lo avvia la 1.6.0 (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /RELAUNCH=1`, più `/LOG`) e chiusura dell'app richiesta | **Riuscita.** L'app non si chiude (finestra della password modale); l'installer aspetta 60 s («Atteso 60000 ms la chiusura di RdpManager»), poi la chiude con Restart Manager (forced), installa (codice 0) e la riapre: exe 1.6.1.0, finestra «Prova sessione 2 - RdpManager 1.6.1», schede «Prova sessione 1» e «Prova sessione 2», stato «2 sessioni aperte». SHA256 di `connections.json` uguale prima e dopo. Log `/LOG` presente. |
| B. Programma di aggiornamento 1.6.1, riuscito | `verifica/aggiornamento.ps1 -Caso riuscito`: copia di riserva, runner `--aggiorna` dalla copia, app chiusa, Setup 1.6.1 con SHA256 giusto | **Riuscita.** Log: chiusura → «Installer verificato con SHA256» → installer codice 0 → «Versione installata verificata: 1.6.1» → «Riaperto RdpManager 1.6.1». Schede ripristinate (2), dati identici, log dell'installer presente. |
| C. SHA256 sbagliato | `-Caso sha`: Setup vero con SHA256 tutto zeri | **Riuscita.** «l'installer non corrisponde più allo SHA256 verificato e non è stato avviato»: nessun log dell'installer (non è mai partito), cartella installata identica (hash di tutti i file), dati identici, 1.6.1 riaperta con le 2 schede e il messaggio «Aggiornamento non riuscito». |
| D. Installer che termina con errore | `-Caso errore`: al posto dell'installer un exe che esce con codice 1 (SHA256 giusto) | **Riuscita.** «l'installer è terminato con il codice 1» → «Versione 1.6.1 rimessa» dalla copia di riserva → riaperta con il messaggio e le 2 schede; cartella e dati identici. |
| E. Errore dopo l'installazione | `-Caso simula` (`--simula-errore`, come `RDPMANAGER_PROVA_AGGIORNAMENTO_FALLITO=1`): Setup vero, poi verifica della versione finta fallita | **Riuscita.** Installer codice 0, «dopo l'installazione la versione è illeggibile» → versione precedente rimessa (cartella identica alla copia) → riaperta con il messaggio e le 2 schede; dati identici. |

Nota: nelle prove B–E l'app è stata terminata dallo script (con la finestra della password aperta non si chiude con
un messaggio di chiusura); il programma di aggiornamento aspetta solo che il processo finisca.
