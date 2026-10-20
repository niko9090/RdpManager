# Novità di RdpManager

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
