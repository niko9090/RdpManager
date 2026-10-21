# Firma del codice con SignPath (gratuita per i progetti open source)

Stato: **preparato, non richiesto**. Nessuna domanda è stata inviata a SignPath e nel repository non c'è nessun
segreto. Il workflow `.github/workflows/firma-signpath.yml` è pronto ma **disattivato** (`if: ${{ false }}`
e solo avvio manuale).

Perché: un eseguibile firmato mostra l'editore verificato invece di «Editore sconosciuto» e, con il tempo, costruisce
la reputazione SmartScreen; è il rimedio più efficace ai blocchi degli antivirus descritti in
[falsi-positivi.md](falsi-positivi.md).

## Prima della domanda

SignPath chiede che nel repository ci sia un file `LICENSE` con una licenza open source riconosciuta (approvata
da OSI). Oggi il file **manca**: la licenza (per esempio MIT) la sceglie Nicholas, e il file va aggiunto prima di
inviare la domanda.

## Cosa offre SignPath Foundation

SignPath Foundation (<https://signpath.org/>) firma gratis i rilasci dei progetti open source che accetta, con un
certificato intestato alla fondazione. Condizioni principali (da ricontrollare sul sito al momento della domanda):

- licenza open source approvata da OSI e codice pubblico;
- progetto attivo e già rilasciato, con un responsabile identificabile;
- build riproducibile da un sistema di CI (GitHub Actions) a partire dal codice del repository: SignPath firma
  solo file prodotti dal CI, non file caricati a mano;
- ogni firma va approvata da una persona del progetto («approver»), con l'accesso a SignPath protetto da 2FA;
- nessun malware, nessun comportamento ingannevole; il software deve dichiarare cosa fa.

## Dati da inviare con la domanda

| Voce | Valore per RdpManager |
|---|---|
| Nome del progetto | RdpManager |
| Repository | <https://github.com/niko9090/RdpManager> (pubblico) |
| Descrizione breve | Gestore di connessioni Desktop remoto per Windows (C# .NET 8, WinForms): sessioni RDP in schede con il controllo ActiveX di Microsoft, profili di credenziali cifrati con DPAPI, aggiornamento verificato con SHA256 |
| Licenza | **da scegliere e aggiungere prima della domanda**: il repository non ha ancora un file `LICENSE` (per esempio MIT) |
| Sito / pagina dei rilasci | <https://github.com/niko9090/RdpManager/releases> |
| Responsabile e approvatore | il proprietario dell'account GitHub `niko9090` (dati personali da indicare solo nel modulo di SignPath, non qui) |
| Sistema di build | GitHub Actions, `windows-latest`, .NET 8 SDK, Inno Setup 6 |
| File da firmare | `RdpManager.exe` e `RdpManager.dll` della pubblicazione a cartella; poi l'installer `RdpManager-Setup-X.Y.Z.exe` (e il disinstallatore che contiene) |
| Cosa fa il programma in rete | connessioni RDP scelte dall'utente, prova TCP sulla porta RDP, Wake-on-LAN su richiesta, API di GitHub per gli aggiornamenti |
| Cosa NON fa | nessun hook di tastiera, nessuna scrittura nel registro, nessuna modifica alle credenziali di Windows, nessun packer od offuscamento |

## Passi (quando si decide di procedere)

1. Aggiungere al repository un file `LICENSE` (licenza OSI) e citarla nel README.
2. Compilare la domanda su <https://signpath.org/apply> con i dati qui sopra.
3. Dopo l'approvazione, in SignPath: creare il **progetto** (slug, es. `RdpManager`), la **signing policy**
   (`release-signing`) e l'**artifact configuration** che descrive i file da firmare nella cartella pubblicata
   (exe e dll di `RdpManager`, non le librerie di Microsoft già firmate).
4. In GitHub → *Settings → Secrets and variables → Actions* (mai nel codice):
   - segreto `SIGNPATH_API_TOKEN` (token di un utente CI di SignPath);
   - variabili `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`,
     `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`.
5. Collegare il repository a SignPath (app GitHub di SignPath), come chiede la loro guida per GitHub Actions.
6. In `.github/workflows/firma-signpath.yml` togliere la riga `if: ${{ false }}`; avviare il workflow a mano
   con la versione, approvare la richiesta in SignPath, scaricare la cartella firmata e l'installer.
7. Firmare anche l'installer con una seconda richiesta (stessa azione, artifact configuration per il Setup).
8. Adattare `scripts/release.ps1`: usare i file firmati del CI, calcolare `SHA256SUMS.txt` **dopo** la firma
   (l'hash cambia) e controllare con `signtool verify /pa /v` prima di pubblicare.

## Cosa resta uguale

- Il rilascio di oggi (`scripts/release.ps1`, installer non firmato, `SHA256SUMS.txt`) continua a funzionare
  finché la firma non è attiva.
- L'aggiornamento automatico dell'app verifica lo SHA256: con i file firmati non cambia nulla per l'utente.
