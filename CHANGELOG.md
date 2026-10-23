# Novità di RdpManager

## 1.6.0

**Cerca PC in rete** (*Strumenti → Cerca PC in rete…*, o il pulsante «Cerca PC» in cima alla barra laterale)
- Una finestra con tre schede, **Rete locale**, **Tailscale** e **Altre VPN**, che elenca i PC con il Desktop
  remoto acceso. **Non parte niente da solo**: la ricerca comincia solo quando premi «Avvia ricerca» (o
  «Leggi la rete Tailscale»), e si può annullare in ogni momento.
- **Scanner di rete**: prova la porta RDP (3389, o quella che scegli) sulla sottorete di una scheda di rete
  attiva oppure su un intervallo scritto a mano, come `192.168.1.0/24`, `192.168.1.10-192.168.1.50` o
  `192.168.1.10-50`. Apre solo una connessione TCP e la chiude subito, con al massimo 64 tentativi insieme e
  un tempo massimo per indirizzo; i nomi arrivano dal DNS inverso.
- **Limiti**: oltre una rete **/22** (1.024 indirizzi) la ricerca chiede conferma, e non va mai oltre una /16.
  Senza conferma si cercano solo indirizzi **privati** (10.x, 172.16-31.x, 192.168.x), **CGNAT** (100.64-127.x)
  e **link-local** (169.254.x); per un intervallo con indirizzi pubblici l'app chiede conferma prima di
  partire. Se la sottorete di una scheda è più grande di una /22 viene proposta la /24 del tuo indirizzo.
- **Tailscale**: legge `tailscale status --json` (solo lettura, non cambia niente in Tailscale) ed elenca i
  dispositivi della tua rete con nome MagicDNS, IP 100.x, sistema operativo, se sono in linea e se il
  Desktop remoto risponde. Le connessioni usano il nome MagicDNS, che resta valido anche se l'IP cambia.
  Se Tailscale non è installato, è spento o non hai fatto l'accesso, un messaggio chiaro dice cosa fare.
- **Altre VPN**: riconosce le schede di rete di Tailscale, ZeroTier, WireGuard, OpenVPN (anche TAP e DCO) e
  di altre VPN (collegamenti PPP, client aziendali come AnyConnect, GlobalProtect, FortiClient) e ne cerca i
  PC nella sottorete. I tipi di VPN sono «fornitori» separati nel Core: aggiungerne uno è una riga.
- **Aggiunta senza doppioni**: si spuntano (o si selezionano) più PC e si aggiungono alla cartella scelta,
  creata se serve («PC trovati in rete» di serie). Un PC già nell'elenco (stesso host o IP, stesso nome
  breve o nome MagicDNS, stessa porta) è segnato «già presente» e non viene aggiunto di nuovo. Le nuove
  connessioni non hanno utente né password: si chiedono alla prima connessione o si impostano con *Modifica*.
- **Reti aziendali**: la ricerca è una piccola scansione delle porte; va usata solo su reti proprie o con il
  permesso di chi le gestisce (i sistemi di sicurezza possono segnalarla). La finestra lo ricorda.

**Compatibilità**: nessun cambiamento al formato di `connections.json` (resta la versione 7); i file dalla
1.0.0 alla 1.5.1 si caricano e i PC trovati si aggiungono senza perdere niente.

## 1.5.1

Correzioni segnalate il 23/10 sulla 1.5.0.

**Monitor successivo nella barra a schermo intero**
- Il pulsante per spostare l'app sull'altro monitor è di nuovo nella barra a schermo intero, a sinistra, a
  destra e in basso, nella sezione «Azioni sulla sessione» (la correzione era pronta dopo la 1.5.0 ma non era
  ancora stata rilasciata: vedi sotto).

**Barre ridimensionabili trascinandone il bordo**
- La barra a schermo intero si allarga o si restringe trascinando il bordo verso la sessione (il bordo
  destro della barra sinistra, il sinistro di quella destra, quello in alto della barra in basso, dove
  si cambia l'altezza). La sessione si adatta subito e, con «Adatta alla finestra», anche la risoluzione.
  Limiti: da 120 a 600 pixel e mai oltre metà dello schermo. La misura resta salvata ed è la stessa di
  *Impostazioni → Schermo intero → Larghezza*.
- Anche la barra laterale della finestra si allarga trascinandone il bordo (da 180 a 600 pixel, lasciando
  almeno 300 pixel alle sessioni), sia fissa sia a comparsa, e la larghezza resta salvata. Corretto un
  difetto per cui, all'avvio, il bordo della barra fissa non si poteva trascinare.

**Esportazione e importazione .rdpm con le password**
- Nell'esportazione la scelta proposta è **«Con le password, cifrate»** quando ci sono password salvate
  (delle connessioni o dei profili di credenziali); il dialogo dice quante sono. Scegliendo «Senza
  password» compare un avviso che dice quante password non andranno nel file.
- Le password dei **profili di credenziali condivisi** viaggiano come quelle delle connessioni, e le
  connessioni restano collegate al loro profilo (anche per il gateway).
- Nell'unione una connessione già presente senza password riceve quella del file; se nel file usava un
  profilo e qui non ne ha, viene collegata al profilo importato. Un profilo già presente senza password
  riceve quella del file. Le password già presenti non vengono mai sostituite.
- Dopo l'importazione un **riepilogo** dice quante connessioni sono state aggiunte, quante erano già
  presenti, quante **password** e quanti **profili di credenziali** sono stati importati.

**Compatibilità**: i connections.json dalla 1.0.0 alla 1.5.0 si caricano senza perdite (la barra laterale
ha la larghezza di prima, 280 pixel); il file passa alla versione 7 quando si salva qualcosa.

**Correzioni della barra a schermo intero (dopo la 1.5.0)**
- Pulsanti rapidi: il nome dell'azione non si sovrappone più alla scorciatoia. Se non ci stanno su una
  riga vanno su due (nome sopra, scorciatoia sotto a destra) e un nome troppo lungo si tronca con «…»;
  vale a sinistra, a destra e in basso, anche alla larghezza minima.
- Tornano raggiungibili a schermo intero **Invia Ctrl+Alt+Canc**, **Foto della sessione** e **Monitor
  successivo** (funzioni della 1.1 che stavano nella barra in alto), nella nuova sezione «Azioni sulla
  sessione», mostrata di serie dopo i pulsanti rapidi e attiva sulla sessione visibile. Chi ha già la
  1.5.0 la trova mostrata senza perdere l'ordine e le scelte delle sue sezioni; se la nasconde, resta
  nascosta.

## 1.5.0

**Schermo intero: in alto non c'è più niente**
- Tolta la barra di passaggio che compariva dal bordo superiore, insieme al rilevamento del mouse su quel
  bordo: la parte alta dello schermo resta della sessione remota.
- Le sessioni connesse (con colore, stato e numero di Ctrl+Alt+1…9) stanno ora nella **barra a schermo
  intero**, che sostituisce a schermo intero la barra laterale della finestra.

**Barra a schermo intero personalizzabile** (*File → Impostazioni → Schermo intero*)
- Posizione: a sinistra (predefinita), a destra o in basso; mai in alto (un valore «alto» in un file
  torna a sinistra).
- Modalità: fissa (la sessione si restringe e non viene mai coperta, con la risoluzione che si adatta),
  a comparsa dal bordo del suo lato, con il ritardo scelto, oppure nascosta (si apre con Ctrl+Alt+B).
- Larghezza (o altezza in basso) e ritardo di comparsa.
- Sezioni scelte e ordinate: sessioni connesse (con miniatura o compatte), preferiti, tutte le
  connessioni con ricerca, spazi di lavoro, pulsanti rapidi, testi rapidi.
- I pulsanti rapidi sono le stesse azioni delle scorciatoie (sessione successiva/precedente, schermo
  intero, barra, Panoramica, ricerca rapida, vista affiancata, blocco), con la combinazione accanto.
- Le impostazioni della barra viaggiano nei .rdpm con «Includi le impostazioni».

**Aggiornando dalla 1.1-1.4** la barra si crea dalle impostazioni di prima: a sinistra, fissa o a
comparsa come la barra laterale, con le sessioni connesse (la vecchia barra in alto) in cima. Un
connections.json dalla 1.0.0 alla 1.4.0 si carica senza perdere nulla.

## 1.4.0

**Spazi di lavoro**
- Salvano le sessioni aperte con il loro layout (ordine delle schede, scheda in primo piano, monitor,
  schermo intero, vista affiancata) e le riaprono con un clic dal menu «Spazi di lavoro» o con una
  scorciatoia propria.

**Blocco con PIN**
- PIN di 4-12 cifre salvato solo come impronta PBKDF2 con salt. Blocco con Ctrl+Alt+L, dal menu o dopo
  un tempo di inattività (predefinito 10 minuti); con il PIN impostato l'app parte bloccata.
- Da bloccata: finestre nascoste, password di nuovo cifrate e non lette, nessuna sessione nuova.
- «Ho dimenticato il PIN»: le connessioni restano, le password salvate si cancellano e vanno reinserite.

**Sessioni**
- Profili di qualità per connessione: automatico, rete locale, connessione lenta (colore, sfondo,
  animazioni, temi, compressione), anche nei file .rdp importati ed esportati.
- Sessione tenuta sveglia: segnale periodico contro le disconnessioni per inattività.

**Elenco e importazione**
- Selezione multipla (Ctrl+clic, Maiusc+clic) con azioni di gruppo: connetti, sposta in cartella,
  assegna un profilo di credenziali, elimina con conferma.
- Importazione da CSV con anteprima e associazione delle colonne (separatore ; o ,, virgolette,
  UTF-8 o Excel), righe non valide e doppioni esclusi.
- F1: riepilogo di tutte le scorciatoie.

**Altro**
- Corretto: la Panoramica poteva far comparire un errore se lo schermo non era copiabile (per esempio
  con la sessione di Windows bloccata).
- docs/firma-signpath.md: dati e workflow pronti (disattivato) per chiedere la firma gratuita SignPath.

Un connections.json dalla 1.0.0 alla 1.3.0 si carica senza perdere nulla. Nessun hook di tastiera,
installer a cartella come nella 1.3.

## 1.3.0


**Antivirus (priorità di questa versione)**
- Su diversi PC l'antivirus bloccava l'installer o l'app. Microsoft Defender in locale non trova minacce
  (né nella 1.2.0 né nella 1.3.0): il problema è la reputazione dei file non firmati e l'analisi
  euristica. Per ridurre i segnali sospetti:
  - **tolto l'hook di tastiera** (WH_KEYBOARD_LL): le scorciatoie dentro la sessione funzionano con
    RegisterHotKey, solo mentre RdpManager è in primo piano;
  - l'installer contiene il programma **a cartella**, non più un exe unico compresso (la versione
    portatile resta un exe unico, non compresso);
  - **metadati** completi nell'exe e nell'installer, manifest **asInvoker**, nessun offuscamento.
- Nuovo **docs/falsi-positivi.md**: testo pronto per segnalare il falso positivo a Microsoft e agli
  altri antivirus, e istruzioni per una futura firma del codice (Azure Trusted Signing o SignPath).

**Aggiornamento automatico con conferma**
- All'avvio e ogni 24 ore controlla l'ultima release, scarica l'installer in
  %LOCALAPPDATA%\RdpManager\updates e ne verifica lo SHA256 con SHA256SUMS.txt; i file non validi
  vengono cancellati.
- Solo dopo la verifica compare un avviso con le novità e tre scelte: «Aggiorna ora» (chiude le
  sessioni, installa in silenzio e riapre l'app con le stesse sessioni), «Alla chiusura», «Più tardi».
- Nelle Impostazioni: automatici con conferma (predefinito), solo avviso, spenti. La versione portatile
  apre la pagina della release.

**Migliorie**
- Errori di connessione spiegati: causa probabile e cosa fare (DNS, PC spento, password sbagliata,
  account bloccato, certificato, gateway…).
- Impostazioni in tre schede: Generale, Scorciatoie, Avvio e aggiornamenti.
- Riordino delle connessioni trascinandole (ordine manuale, oppure per nome).
- Esportazione e importazione .rdpm anche delle impostazioni dell'app (a scelta).
- Log a rotazione in %LOCALAPPDATA%\RdpManager\logs, senza password né indirizzi dei PC (questi solo
  con il log dettagliato).

Un connections.json della 1.0.0, 1.1.0 o 1.2.0 si carica senza perdere nulla.

Da provare a mano (vedi «Prova manuale 1.3» nel README): installazione sopra la 1.2.0 sui PC dove
l'antivirus bloccava, aggiornamento automatico verso la versione successiva, messaggi di errore con PC
veri. La prova di aggiornamento silenzioso (verifica/installer.ps1 -Aggiornamento) va lanciata su un PC
pulito.

## 1.2.0


**Trovare e vedere le sessioni**
- Panoramica (Ctrl+Alt+G): griglia di miniature dal vivo delle sessioni connesse, anche a schermo
  intero; clic, Invio o il numero 1-9 per passare alla sessione.
- Ricerca rapida (Ctrl+Alt+Spazio): ricerca approssimata su nome, host ed etichetta (anche con un
  errore di battitura); Invio passa alla sessione aperta o ne apre una nuova.
- Vista affiancata (Ctrl+Alt+S): due sessioni nella stessa finestra con divisione regolabile e
  ricordata, senza riconnettere.

**Credenziali e accesso**
- Profili di credenziali condivisi: utente, dominio e password (cifrata DPAPI) salvati una volta e
  usati da più connessioni, anche per il gateway. Nei file .rdpm seguono le stesse regole delle
  password: esclusi oppure cifrati con la password del file.
- Remote Desktop Gateway per connessione (host del gateway, credenziali uguali o da un profilo),
  anche nei file .rdp importati ed esportati (nuovo: clic destro → Esporta come file .rdp).
- Importazione della cronologia di mstsc (File → Importa dalla cronologia di mstsc): letta dal
  registro HKCU in sola lettura e solo quando lo chiedi.

**Lavorare nelle sessioni**
- Testi rapidi: frammenti salvati che un clic digita nella sessione attiva (menu Sessione, «Invia»,
  «Testi» a schermo intero).
- Cronologia delle connessioni (Ctrl+H): inizio, fine, durata e motivo della disconnessione, con
  filtro per connessione e svuotamento; si può disattivare.

**L'app**
- Icona nell'area di notifica con il menu delle connessioni; volendo, la X lascia l'app lì.
- Scorciatoia globale per richiamare l'app da qualunque programma (predefinita Ctrl+Alt+Maiusc+R,
  RegisterHotKey).
- Tema chiaro, scuro o come Windows.
- Aggiornamento con un clic: scarica l'installer dell'ultima release, ne verifica lo SHA256 con
  SHA256SUMS.txt della release e lo avvia solo dopo la tua conferma; se la verifica fallisce non
  avvia nulla. Da questa versione ogni release contiene SHA256SUMS.txt.
- Impostazioni divise in «Generale» e «Scorciatoie».

Un connections.json della 1.0.0 o della 1.1.0 si carica senza perdere nulla.

Da provare a mano con PC veri (vedi «Prova manuale 1.2» nel README): miniature di sessioni davvero
connesse, un gateway vero, testi rapidi in un PC remoto, aggiornamento con un clic dalla release
successiva.

## 1.1.0

Pensata per chi lavora con più PC remoti insieme.

**Schermo intero dell'app e passaggio rapido fra le sessioni**
- Ctrl+Alt+Invio (o F11): RdpManager diventa una finestra senza bordi sul monitor scelto, con tutte
  le sessioni dentro.
- Barra di passaggio che compare dal bordo in alto: sessioni con colore e stato, clic per passare,
  più Ctrl+Alt+Canc, Foto, Barra laterale, Monitor › ed Esci.
- Scorciatoie che funzionano anche con il fuoco nella sessione remota: Ctrl+Alt+Destra/Sinistra
  (sessione connessa successiva/precedente), Ctrl+Alt+1…9, Ctrl+Alt+Invio, Ctrl+Alt+B. Si cambiano
  in File → Impostazioni. L'hook di tastiera è attivo solo mentre RdpManager è in primo piano.

**Barra laterale**
- Fissa o a comparsa dal bordo sinistro, disponibile anche a schermo intero.
- Preferiti e Recenti in cima, stato di ogni connessione (connessa, in connessione, disconnessa,
  raggiungibile o no sulla porta RDP), colore ed etichetta per connessione.
- Connetti tutta la cartella; Accendi (Wake-on-LAN) con l'indirizzo MAC della connessione.

**Più monitor**
- Scelta del monitor per lo schermo intero (ricordato all'avvio), «Usa tutti i monitor» per
  connessione, adattamento automatico a dimensione e scala (DPI) senza riconnettere, spostamento
  delle finestre staccate da un monitor all'altro.

**Esportazione e importazione (.rdpm)**
- File → Esporta/Importa connessioni: senza password oppure con le password cifrate da una password
  del file (AES-256-GCM, chiave da PBKDF2-SHA256). Nessuna password in chiaro nel file.
- Importazione con «Unisci» (senza doppioni) o «Sostituisci».

**Tra il PC locale e quelli remoti**
- Invia → Incolla appunti come digitazione, Ctrl+Alt+Canc, Foto della sessione (PNG in
  Immagini\RdpManager e negli appunti).
- Opzioni per connessione: audio (qui / sul PC remoto / nessuno), stampanti, microfono.

**Altro**
- Riconnessione automatica con un numero massimo di tentativi.
- Riapertura delle sessioni all'avvio (File → Impostazioni).
- Un `connections.json` della 1.0.0 si carica senza perdite.

Da provare a mano con PC veri (vedi «Prova manuale 1.1» nel README): sessione connessa con
password, DPI con due monitor veri, Ctrl+Alt+Canc, incolla come digitazione, foto,
audio/stampanti/microfono, Wake-on-LAN verso un PC vero.

## 1.0.0

Prima versione: elenco connessioni con cartelle, sessioni in schede con il controllo ActiveX RDP,
Ctrl+Tab, Riconnetti, finestra staccata, «Apri con mstsc», password cifrate con DPAPI, importazione
`.rdp`, ricerca, installer e controllo aggiornamenti.
