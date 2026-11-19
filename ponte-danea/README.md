# Ponte Danea

Programma a riga di comando che prende le **fatture di vendita** dall'archivio Easyfatt (Danea) e le manda al gestionale GLOS.
Sostituisce il vecchio programma sul PC della segretaria: GLOS si aspetta ancora lo stesso pacchetto.
È separato da RdpManager (non è in `RdpManager.sln`).

## Cosa fa

1. Copia l'archivio `.eft` in una cartella temporanea e lo apre **senza server** (Firebird 2.5 embedded). L'originale non si tocca; la copia si cancella alla fine.
2. Legge le fatture dei tipi `I` (fattura), `J` (acconto), `N` (nota di credito), `F` (altra fattura), con righe, scadenze e clienti.
   Il filtro è sulla data del documento; entrano anche le fatture più vecchie che hanno un incasso registrato nel periodo.
3. Scrive lo zip nel formato di GLOS (2.140.x): `documenti.csv`, `righe.csv`, `scadenze.csv`, `soggetti.csv`, `sedi.csv`, `articoli.csv` e `info.json`, tutti alla radice.
   CSV con `;`, UTF-8 con BOM, date `AAAA-MM-GG`, decimali col punto. `sedi.csv` e `articoli.csv` hanno solo l'intestazione (GLOS li tratta come facoltativi).
   In `soggetti.csv` ci sono solo i clienti delle fatture nello zip.
   `pagato_il` si riempie **solo** con `Saldato = 1` (in Danea `DataPagam` c'è anche sulle scadenze aperte).
4. Lo spedisce con `POST <url>/api/danea/pacchetto` (campo `pacchetto`, intestazione `Authorization: Bearer <token>`).

## Preparazione

- .NET 8 (SDK per compilare, runtime per usarlo).
- Firebird 2.5.9 **embedded a 64 bit** dallo zip ufficiale (`Firebird-2.5.9.*_x64_embed.zip`, firebirdsql.org): decomprimerlo in una cartella e rinominare `fbembed.dll` in `fbclient.dll`. Le altre dll e `firebird.msg` restano accanto.
- Copiare `ponte-danea.esempio.json` in `ponte-danea.json` (accanto al programma, o altrove con `--config`) e compilarlo:
  `archivio`, `libreriaFirebird`, `url`, `token` (si crea in GLOS, Impostazioni → Danea), `nomePc`, `giorniIndietro`.
  Il file con il token non va mai nel repository (è nel `.gitignore`).

## Uso

```
dotnet run --project src/PonteDanea -- --config ponte-danea.json --prova --giorni 30
```

| Opzione | Significato |
|---|---|
| `--prova` | prepara lo zip e **non lo spedisce** (lo salva in `ponte-danea-prova.zip`); non servono `url` e `token` |
| `--giorni N` | fatture degli ultimi N giorni (default `giorniIndietro`, 30) |
| `--da AAAA-MM-GG` / `--fino AAAA-MM-GG` | intervallo di date |
| `--zip file.zip` | dove salvare lo zip (anche quando si spedisce) |
| `--config file.json` | file di configurazione |

Codici di uscita: 0 tutto bene, 1 errore di configurazione o archivio, 2 GLOS non raggiungibile o pacchetto rifiutato.

## Test

```
dotnet test tests/PonteDanea.Tests/PonteDanea.Tests.csproj
```

I test usano un archivio finto in memoria (interfaccia `IArchivioDanea`): niente Firebird, niente rete.

## Dove farlo girare

`192.168.1.250` si vede solo dalla rete d'ufficio. Conviene un **PC Windows dell'ufficio sempre acceso**, con un'attività pianificata
(Utilità di pianificazione) ogni notte, per esempio alle 02:00: `PonteDanea.exe --config C:\PonteDanea\ponte-danea.json --giorni 30`.
Il PC legge l'archivio dalla condivisione del NAS e parla con GLOS sull'indirizzo interno. Meglio di notte, quando Easyfatt è chiuso, così la copia del file è coerente.
