# homeBrain
Home platform — unified dashboard for energy, appliances and household expenses
# Casa Timò — Piattaforma di gestione della casa
> Documento di progetto per sviluppo vibe coding  
> Aggiornato: giugno 2026

---

## Contesto e obiettivo

Sviluppo di una **web app domestica unificata** in C# / Blazor che:
- Raccoglie e storicizza i dati degli impianti di casa (fotovoltaico, pompa di calore, wallbox)
- Gestisce bollette e spese domestiche leggendo automaticamente la posta Gmail
- Mostra dashboard interattive con grafici storici
- Invia reminder per scadenze (TARI, bollette, manutenzioni impianti)
- È accessibile da mobile come PWA, ovunque

**Filosofia:** sensori come risorse condivise tra servizi indipendenti (sensor fusion), architettura espandibile nel tempo (telecamere AI, nuovi sensori, attuatori).

> **Nota — due modelli di condivisione diversi.** Per i sensori a valore singolo (temperature, potenze, SOC batteria, ecc.) la condivisione è gratuita: la fonte pubblica una volta su MQTT e un numero qualsiasi di servizi si abbona allo stesso topic senza costo aggiuntivo (fan-out). Per le **telecamere** non vale lo stesso: il flusso video è dati binari continui ad alto bitrate, che MQTT non può trasportare — ogni servizio che ha bisogno dei frame (pipeline AI di sorveglianza, live view HLS, un eventuale futuro consumer) deve aprire una **propria connessione diretta** (RTSP o snapshot HTTP) alla telecamera. Qui il costo cresce con il numero di consumatori, ed è limitato dal numero massimo di stream RTSP concorrenti supportati dalla camera — non è un sensore "condiviso via software" ma una risorsa fisica condivisa a livello di rete. Vedi STEP 13 e STEP 14 per il dettaglio.

---

## Impianti e dispositivi presenti

| Dispositivo | Modello | Protocollo / API |
|---|---|---|
| Pompa di calore | Viessmann Vitocal 222-S | API REST `api.viessmann-climatesolutions.com` |
| Fotovoltaico + Batteria | Huawei + LUNA 2000 (20 kWh) | Modbus TCP locale (LAN) — cloud FusionSolar API opzionale, richiede permessi installer |
| Clima | Daikin 5MXM 90N multisplit | Daikin Cloud API |
| Wallbox | Gewiss GWJ3002A 7kW | OCPP |
| Telecamere | Reolink (da acquistare, max 6) | RTSP/ONVIF — 1-2 esterne PoE, 3-4 interne WiFi |
| VMC | Viessmann Vitovent 100-D | Viessmann API |
| Email bollette | Gmail | Gmail API (OAuth2) |

---

## Stack tecnologico

| Layer | Tecnologia | Note |
|---|---|---|
| Frontend | Blazor WebAssembly (.NET 10) | PWA, accessibile da mobile |
| API | ASP.NET Core Minimal API | REST + JWT auth |
| Worker services | C# Background Services | Connettori e BillWatcher |
| Sidecar Viessmann | Python 3.12 + requests | Polling PDC → MQTT (headless PKCE) |
| Message broker | MQTTnet embedded (.NET) | Broker in-process nel Workers, porta 1883 |
| Database | SQLite condiviso (`data/casatimo.db`) | EF Core, unico file per API + Workers |
| Storage PDF | NAS Synology | Cartelle per anno/tipo |
| Hosting | Mini PC locale | Docker Compose |
| Accesso remoto | Cloudflare Tunnel | Zero config router |

---

## Struttura del progetto

```
homeBrain/
├── CasaTimo.sln
├── docker-compose.yml
├── .env.example                      # template variabili d'ambiente
├── .env                              # secrets reali (NON committare)
├── data/
│   └── casatimo.db                   # SQLite condiviso API + Workers
├── sidecar-viessmann/                # sidecar Python Vitocal 222-S
│   ├── main.py                       # polling loop → MQTT
│   ├── setup_token.py                # autenticazione one-time (PKCE)
│   ├── requirements.txt
│   ├── Dockerfile
│   └── data/viessmann_token.json     # token OAuth2 (NON committare)
├── sidecar-huawei/                   # sidecar Python inverter Huawei
│   ├── main.py                       # polling Modbus TCP locale → MQTT
│   ├── requirements.txt
│   └── Dockerfile
│
└── src/
    ├── CasaTimo.Web/                 # Blazor WebAssembly (frontend)
    ├── CasaTimo.Api/                 # ASP.NET Core Minimal API
    ├── CasaTimo.Workers/             # Background services (C#)
    ├── CasaTimo.Core/                # Modelli condivisi
    ├── CasaTimo.Infrastructure/      # DB context, MQTT client, connettori
    └── CasaTimo.Api.Tests/           # Test di integrazione xUnit (7 test)
```

---

## Architettura — flusso dati attuale

```
[Vitocal 222-S]
      │ API REST Viessmann (PKCE OAuth2)
      ▼
[sidecar-viessmann Python]  ──→  [MQTT Broker — MQTTnet embedded :1883]
                                          │
                              ┌───────────┤
                              │           │
                    [HistoryRecorder]   [altri sidecar futuri]
                    (C# BackgroundSvc)  (Huawei, Daikin, Wallbox)
                              │
                              ▼
                    [data/casatimo.db]  ←── EF Core SQLite (condiviso)
                              │
                              ▼
                    [ASP.NET Core API :5233]
                    GET /api/sensors/live
                    GET /api/sensors/history
                    GET /api/sensors/devices
                              │
                              ▼
                    [Blazor WebAssembly :5288]
                    Dashboard · Bollette · Reminder

[Gmail] → [BillWatcher (futuro)] → [data/casatimo.db] + [PDF su NAS]
```

---

## Avvio in sviluppo

```bash
# 1. Workers (avvia anche il broker MQTT embedded su :1883)
dotnet run --project src/CasaTimo.Workers

# 2. API backend (terminale 2)
dotnet run --project src/CasaTimo.Api

# 3. Frontend Blazor (terminale 3)
dotnet run --project src/CasaTimo.Web

# 4. Sidecar Docker (si connettono al broker sul host via host.docker.internal:1883)
docker compose up viessmann-sidecar
docker compose up huawei-sidecar        # modalità modbus di default, vedi STEP 5
```

---

## Step di sviluppo

### STEP 1 — Scaffolding del progetto ✅ Completato
Struttura solution con 5 progetti (`Core`, `Infrastructure`, `Api`, `Workers`, `Web`), docker-compose con Mosquitto, pagina placeholder Blazor, endpoint `/health`.

---

### STEP 2 — Modelli dati (Core) ✅ Completato
```csharp
SensorReading { Id, DeviceId, Metric, Value, Unit, Timestamp }
Device { Id, Name, Type, Location, IsActive }
Bill { Id, Type, Issuer, Amount, DueDate, PeriodFrom, PeriodTo, PdfPath, EmailId, CreatedAt, IsPaid }
Reminder { Id, BillId, DueDate, DaysBefore, IsSent }
MaintenanceRecord { Id, DeviceId, Description, Date, Cost, NextDueDate }
ConnectorConfig { Id, ConnectorName, SettingsJson, UpdatedAt }
```
`CasaTimoDbContext` (EF Core + SQLite) con indici su `SensorReading(DeviceId, Timestamp)` e unique su `Bill.EmailId`.  
DB condiviso tra API e Workers: `data/casatimo.db` (percorso `../../data/casatimo.db` relativo a ciascun progetto).

---

### STEP 3 — MQTT infrastructure ✅ Completato
`MqttClientService` (MQTTnet) registrato come singleton con `MessageReceived` event per i consumer interni.  
`MqttBrokerService` (MQTTnet Server embedded) avviato come `IHostedService` nel Workers, porta 1883.  
I sidecar Docker si connettono tramite `host.docker.internal:1883`.

**Topic conventions:**
```
casatimo/{deviceId}/{metric}     payload: {"value": 42.5, "unit": "°C"}

casatimo/pdc/outdoor_temp        temperatura esterna
casatimo/pdc/return_temp         temperatura ritorno impianto
casatimo/pdc/supply_temp         temperatura mandata
casatimo/pdc/dhw_temp            acqua calda sanitaria
casatimo/pdc/mode                modalità operativa (stringa)
casatimo/pdc/compressor_active   0/1
casatimo/fv/*                    dati fotovoltaico (futuro)
casatimo/wallbox/*               dati wallbox (futuro)
```

---

### STEP 4 — Sidecar Viessmann (PDC) ✅ Completato (sidecar Python)
Sidecar Python indipendente che bypassa PyViCare e accede direttamente alle API REST Viessmann.

**Autenticazione:** PKCE + HTTP Basic auth headless su `iam.viessmann-climatesolutions.com`.  
**Developer portal:** `developer.viessmann-climatesolutions.com` (registra app con redirect URI `vicare://oauth-callback/everest`).  
**Setup one-time:** `python setup_token.py` — salva `data/viessmann_token.json` con refresh token.  
**Dati letti dalla Vitocal 222-S:**
- Installation ID: `2949264` | Gateway: `7637415018351230` | Device: `0` (CU401B_S)
- Temperatura esterna, ritorno impianto, ACS, modalità, stato compressore

**Note tecniche:**
- `iam.viessmann.com` è deprecato — usare `iam.viessmann-climatesolutions.com`
- Scope `IoT offline_access` (non `IoT User` che dà 400)
- Il C# `ViessmannConnector` in Workers rimane come scaffold per futura integrazione
- **Non esiste alternativa locale:** il modulo ViCare comunica solo via cloud Viessmann. Optolink (seriale) e CAN bus (progetto `open3e`) richiedono hardware aggiuntivo e non sono supportati sulla Vitocal 222-S.

---

### STEP 5 — Connettore Huawei FusionSolar ✅ Completato (sidecar Python, doppia modalità)

Sidecar Python `sidecar-huawei/` con due modalità selezionabili via `HUAWEI_MODE`, entrambe pubblicano gli stessi topic MQTT.

**Modalità `modbus` (default)** — accesso locale via `python-huawei-solar`, nessun account cloud richiesto:
- L'inverter è raggiungibile in LAN (es. via cavo Ethernet al router) → connessione diretta `create_tcp_bridge(host, port=6607)`
- Prerequisito: Modbus TCP abilitato dall'app FusionSolar (`Dispositivi` → `Impostazioni` → `Configurazione comunicazione`) — **ancora da fare sull'impianto**
- Legge `ACTIVE_POWER`, `DAILY_YIELD_ENERGY` sempre; `STORAGE_STATE_OF_CAPACITY`/`STORAGE_CHARGE_DISCHARGE_POWER` solo se `bridge.battery_type != NONE`; `POWER_METER_ACTIVE_POWER`/`LOAD_POWER` solo se `bridge.power_meter_online`
- Variabili `.env`: `HUAWEI_INVERTER_HOST` (IP locale, es. `192.168.1.100`), `HUAWEI_INVERTER_PORT` (default `6607`)

**Modalità `cloud` (opzionale, tenuta come fallback futuro)** — northbound API FusionSolar:
- Richiede un account con permessi "installer", da richiedere separatamente — non disponibile ora
- Autenticazione: POST `/thirdData/login` con `userName` + `systemCode`, re-login automatico su failCode 401
- Discovery: `getStationList` → stationCode, `getDevList` → device ID per tipo (inverter=1, batteria=39, meter=47)
- Poll: `getStationRealKpi`, `getDevRealKpi` per inverter/batteria/meter
- Variabili `.env`: `FUSIONSOLAR_USER`, `FUSIONSOLAR_SYSCODE`

**Topic MQTT (comuni alle due modalità):**
```
casatimo/fv/power_active     kW   produzione FV istantanea
casatimo/fv/energy_today     kWh  energia prodotta oggi
casatimo/fv/battery_soc      %    SOC batteria LUNA 2000 (solo se presente)
casatimo/fv/battery_power    kW   potenza batteria (+ carica, - scarica) (solo se presente)
casatimo/fv/grid_power       kW   potenza rete (+ export, - import) (solo se power meter presente)
casatimo/fv/load_power       kW   consumo casa istantaneo (solo se power meter presente)
```

**Da fare prima del primo avvio reale in modalità modbus:**
1. Abilitare Modbus TCP dall'app FusionSolar sull'inverter
2. Recuperare l'IP locale dell'inverter sulla LAN e impostarlo in `HUAWEI_INVERTER_HOST`
3. Verificare a runtime i log di `bridge.battery_type`/`bridge.power_meter_online` per confermare che batteria e meter vengano rilevati correttamente

---

### STEP 6 — HistoryRecorder ✅ Completato + testato end-to-end
`HistoryRecorder` (`BackgroundService` C#):
- Aspetta che `MqttClientService.IsConnected == true` (max 30s)
- Si abbona a `casatimo/#` via `MessageReceived` event
- Parsa topic `casatimo/{deviceId}/{metric}` e payload JSON/numerico
- Salva `SensorReading` su SQLite con `IServiceScopeFactory`

**Verificato:** dati reali della Vitocal 222-S salvati nel DB `data/casatimo.db`.

---

### STEP 7 — BillWatcher (Gmail → PDF → DB) ⬜ Da fare

**Prompt suggerito:**
> "Crea `BillWatcher` come `BackgroundService`. Usa `Google.Apis.Gmail.v1` per leggere le email. Filtra per mittenti configurati. Scarica PDF allegati. Usa iTextSharp per estrarre il testo. Chiama Claude API (`claude-sonnet-4-6`) passando il testo del PDF per estrarre: importo, scadenza, periodo_da, periodo_a, consumi_kwh. Salva su SQLite e PDF su path NAS."

---

### STEP 8 — API endpoints ✅ Parziale

**Implementati:**
```
GET  /                               → info versione
GET  /health                         → stato servizio
POST /api/auth/token                 → login → JWT Bearer token
GET  /api/sensors/devices            → lista dispositivi/metriche con conteggio
GET  /api/sensors/live               → ultimo valore per ogni deviceId/metric
GET  /api/sensors/history            → storico con filtri (deviceId, metric, from, to, limit)
GET  /api/connectors                 → lista configurazioni connettori
GET  /api/connectors/{name}          → singola configurazione
PUT  /api/connectors/{name}          → aggiorna (richiede JWT)
```

**Da aggiungere:**
```
GET  /api/bills  (+ /{id}/pdf, /{id}/paid)
GET  /api/reminders
GET  /api/maintenance
POST /api/maintenance
WS   /ws/live    (SignalR real-time)
```

---

### STEP 9 — Dashboard domotica (Blazor) ⬜ Da fare

**Componenti da creare:**
- `HeatPumpCard` — temperature PDC, modalità, stato compressore (dati da `/api/sensors/live`)
- `EnergyFlowCard` — flusso FV → batteria → casa → rete (animato, dati futuri)
- `BatteryCard` — SOC con barra e trend
- `ProductionChart` — grafico storico (ApexCharts + `/api/sensors/history`)
- `LiveIndicator` — polling ogni 60s con `HttpClient`

**Prompt suggerito:**
> "In CasaTimo.Web aggiungi la pagina `/impianti`. Crea `HeatPumpCard.razor` che chiama `http://localhost:5233/api/sensors/live`, filtra i record con DeviceId=pdc e mostra temperatura esterna, ritorno, ACS, modalità e stato compressore in Bootstrap card. Aggiorna ogni 60 secondi con un Timer."

---

### STEP 10 — Sezione bollette e spese (Blazor) ⬜ Da fare

---

### STEP 11 — PWA e accesso remoto ⬜ Da fare
- `manifest.json` in Blazor WASM
- Service worker per offline
- Cloudflare Tunnel sul mini PC
- Dominio personalizzato (es. `casa.timo.dev`)

---

### STEP 12 — Reminder e notifiche ⬜ Da fare
- Web Push API, Telegram bot, o Email

---

### STEP 13 — Videosorveglianza con AI leggera ⬜ Da fare

**Hardware previsto:** 1-2 telecamere esterne + 3-4 interne, max 6 totali.

**Requisiti hardware telecamere:**
- Protocollo **RTSP nativo** (ONVIF compatibile)
- **Almeno 2 stream RTSP concorrenti supportati** — la pipeline AI e la live view HLS aprono ciascuna una propria connessione RTSP indipendente verso la stessa camera (vedi sotto), quindi verificare per ogni modello il numero massimo di client RTSP simultanei prima dell'acquisto
- Se disponibile un "sub stream" a bassa risoluzione (es. 640×360) separato dal main stream, va usato per la pipeline AI — riduce CPU di decodifica/motion, tanto YOLO ridimensiona comunque a 640×640 internamente
- Evitare telecamere cloud-only (Tuya, Wyze senza hack, ecc.)
- **Consigliato: Reolink**
  - Interno: E1 Pro o E1 Outdoor (WiFi 2K, ~35€)
  - Esterno: RLC-510A o RLC-810A (PoE, IP66, un cavo per dati + alimentazione)
  - Per le esterne preferire PoE: serve uno switch PoE (~30€)
- Risoluzione ottimale per l'AI: **1080p/2K** (il modello YOLO ridimensiona a 640×640 internamente, il 4K spreca CPU senza migliorare l'accuracy)

**Architettura — tutto in C#/.NET, un solo processo Worker per tutte le camere:**
```
Telecamere (RTSP — 2 connessioni indipendenti per camera)
      │
      ├──────────────────────────────┐
      ▼                               ▼
[Pipeline AI, per camera]        [Live view]
CasaTimo.Camera (Worker Service)  FFmpeg → HLS (.ts/.m3u8)
  ├── OpenCvSharp VideoCapture         │
  │   → cattura frame via RTSP         ▼
  ├── Motion filter (MOG2,       Blazor + hls.js
  │   OpenCvSharp) → bounding    (live view, latenza ~3-5s)
  │   box delle regioni cambiate
  ├── Crop finestre in movimento
  │   non già coperte da un track
  ├── YOLOv8n ONNX (Microsoft.ML.OnnxRuntime)
  │   → inferenza solo sulle finestre
  ├── Match detection↔track esistenti (IoU)
  │   → nuovo track (CSRT) o aggiorna esistente
  ├── CSRT tracker.Update() ogni frame
  │   tra un'inferenza YOLO e l'altra
  ├── Track perso (N miss consecutivi)
  │   → chiude l'evento
  ├── Salva JPEG su NAS a 2 FPS durante il track attivo
  └── Pubblica MQTT (solo eventi, mai i frame):
        casatimo/cameras/{id}/motion   {"active": true/false}
        casatimo/cameras/{id}/person   {"confidence": 0.87, "count": 1}
              │
              ▼
        MQTT → HistoryRecorder → SQLite (CameraEvent, nuova entità)
```

**Nota:** MQTT trasporta solo i JSON di evento (motion/person/vehicle/status), mai il flusso video: non è adatto a streaming continuo ad alto bitrate. La pipeline AI e la live view HLS si collegano entrambe direttamente alla telecamera via RTSP, indipendentemente l'una dall'altra.

**AI: YOLOv8 Nano ONNX**
- Modello pre-addestrato COCO (persone, auto, animali, 80 classi)
- Dimensione: ~6MB, inference su CPU: ~50-100ms a 640×640
- Libreria: `Microsoft.ML.OnnxRuntime` (nessuna dipendenza GPU)
- Inferenza solo sulle finestre segnalate dal motion filter (o su frame intero se il movimento copre gran parte dell'inquadratura), non ad ogni frame
- YOLO conferma se il motion è causato da persona/veicolo/animale, riducendo falsi positivi

**Motion detection + tracking: `OpenCvSharp`**
- Motion filter: background subtractor **MOG2** (più robusto a ombre/variazioni di luce del semplice frame diff, comunque leggero)
- Tracker per-oggetto confermato da YOLO: **CSRT** — segue l'oggetto frame per frame senza richiamare YOLO, il track viene rimosso solo quando il tracker lo perde (N miss consecutivi)
- Dipendenza nativa (~100-150MB nell'immagine Docker): scelta perché CSRT e la cattura RTSP sono già forniti dallo stesso pacchetto; **in futuro, se si volesse eliminare la dipendenza nativa**, si può valutare un MOG2 scritto a mano in C# (algoritmo per-pixel, fattibile) più un tracker più semplice (es. correlazione/optical-flow) al posto di CSRT — non prioritario ora

**Registrazione:**
- Solo durante un track attivo (no registrazione continua)
- 1 frame ogni 500ms (2 FPS) salvato come JPEG su NAS Synology (`/mnt/nas/casatimo/cameras/{id}/YYYY-MM-DD/`)
- Retention configurabile (es. 30 giorni, poi auto-delete)
- Evento loggato su SQLite in una nuova entità `CameraEvent` (timestamp, camera ID, tipo oggetto, confidence, path JPEG) — non riutilizza `SensorReading`, che modella singoli valori numerici

**Live view in Blazor:**
- FFmpeg transcoding RTSP → HLS (segmenti .ts ogni 2s), connessione RTSP separata da quella della pipeline AI
- Player HLS nel browser via `hls.js`
- Latenza attesa: 3-6s (accettabile per sorveglianza)
- Alternativa futura a bassa latenza: WebRTC (più complesso)

**Topic MQTT:**
```
casatimo/cameras/{id}/motion    {"active": true, "timestamp": "..."}
casatimo/cameras/{id}/person    {"confidence": 0.87, "count": 1, "timestamp": "..."}
casatimo/cameras/{id}/vehicle   {"confidence": 0.91, "count": 1, "timestamp": "..."}
casatimo/cameras/{id}/status    {"online": true, "fps": 12.3}
```

**Variabili `.env` da aggiungere per ogni camera:**
```
CAMERA_01_NAME=ingresso
CAMERA_01_RTSP=rtsp://admin:password@192.168.1.x:554/stream1
CAMERA_02_NAME=giardino
CAMERA_02_RTSP=rtsp://admin:password@192.168.1.y:554/stream1
```

**Note:**
- Il mini PC (hosting) deve avere CPU sufficiente: ~15% core per camera a 1080p con YOLOv8n
- Con 5 telecamere stimate: ~75% di un core fisico (dipende dall'hardware)
- Il NAS DS115j (ARM 32bit, no Docker) viene usato solo come storage SMB montato sul mini PC
- Deployment: un solo processo/container `CasaTimo.Camera`, un loop async per camera configurata — più semplice da gestire di un container per camera, a scapito dell'isolamento in caso di crash di una singola camera

---

### STEP 14 — Valutazione altezza erba giardino ⬜ Da fare (metodo da decidere)

Riusa la camera esterna già prevista per la videosorveglianza (STEP 13), ma **non** tramite la pipeline AI in tempo reale: essendo la crescita dell'erba un fenomeno lento (giorni, non secondi), basta uno **snapshot HTTP periodico** (es. ogni 1-6 ore) invece di una connessione RTSP persistente — la maggior parte delle IP cam (incluse le Reolink) espone un endpoint HTTP separato per un singolo JPEG on-demand (es. `/cgi-bin/api.cgi?cmd=Snap` su Reolink). Questo non compete con gli stream RTSP persistenti di STEP 13 e non richiede requisiti hardware aggiuntivi.

**Deployment:** un `BackgroundService` in `CasaTimo.Workers` (non in `CasaTimo.Camera`) — servizio indipendente dalla sorveglianza, stesso pattern degli altri worker.

**Metodo di misura — due opzioni, da scegliere:**

1. **Marker graduato in prato (più affidabile, richiede setup fisico)** — un paletto/righello a bande colorate piantato fisso nel prato, nell'inquadratura della camera. Ad ogni snapshot: individua il marker via color segmentation, trova il punto in cui l'erba lo occlude, converte la distanza in pixel in cm usando la scala nota del marker. Tecnica robusta alla luce (confronta due colori nello stesso frame), ma richiede di piantare e mantenere fisicamente il marker (non spostarlo, non farlo urtare dal tosaerba).
2. **Indice di verde relativo, senza marker (setup zero, meno preciso)** — confronta ogni snapshot con una baseline scattata subito dopo un taglio, calcolando un indice di vegetazione (es. Excess Green Index) su una porzione fissa di prato. Non dà un'altezza in cm, solo un trend → oltre una soglia empirica scatta "serve tagliare". Molto sensibile a luce/ombre/rugiada, andrebbe normalizzato rispetto a una zona di riferimento non-erba nello stesso frame, e ricalibrato dopo ogni taglio.

**Vincoli comuni a entrambe le opzioni:**
- Inquadratura fissa: se la camera si sposta anche di poco la calibrazione salta
- Standardizzare l'orario dello snapshot (es. sempre alle 12:00 solari) per ridurre la variabilità delle ombre
- La camera esterna scelta deve inquadrare il prato e supportare l'endpoint di snapshot HTTP (da verificare sul modello specifico)

**Topic MQTT (indicativo, dipende dal metodo scelto):**
```
casatimo/garden/grass_height    cm    (opzione marker)
casatimo/garden/needs_mowing    bool  (opzione indice verde)
```

**Nota sul modello dati:** il risultato può alimentare `HistoryRecorder` → SQLite come un sensore qualsiasi. Un eventuale promemoria "serve tagliare" non può riusare `Reminder` così com'è, perché oggi è legato a `Bill` (`Reminder.BillId`) — andrebbe generalizzato per notifiche non collegate a una bolletta.

---

## Note architetturali

### Sicurezza
- Tutti i secret in `.env` (mai nel codice) — vedi `.env.example`
- JWT Bearer auth su endpoint di scrittura
- `sidecar-viessmann/data/viessmann_token.json` escluso da git (`.gitignore`)
- Broker MQTT embedded: nessuna auth in sviluppo — aggiungere `WithDefaultEndpointCredentials` in produzione
- CORS ristretto a `AllowedOrigins` in `appsettings.json`

### Configurazione credenziali
```bash
# Sviluppo — dotnet user-secrets
cd src/CasaTimo.Api
dotnet user-secrets set "AdminPassword" "la_tua_password"
dotnet user-secrets set "Jwt:Key" "chiave_min_32_caratteri"

# Sidecar Viessmann
set VIESSMANN_USER=email@example.com
set VIESSMANN_PASS=password
set VIESSMANN_CLIENT_ID=<dal developer portal>
```

### Database
- Unico file `data/casatimo.db` condiviso tra API e Workers
- Path relativo `../../data/casatimo.db` dal working directory di ciascun progetto
- Per Docker/produzione: sovrascrivere via env `CASATIMO_API__ConnectionStrings__CasaTimoDb`

### NAS Synology
- DS115j non compatibile Docker (ARM 32bit) — usare come storage SMB
- Montare su mini PC: `/mnt/nas/casatimo/`

### Frequenze di aggiornamento
| Dato | Frequenza polling | Storage |
|---|---|---|
| Pompa di calore (PDC) | 5 min (sidecar) | ad ogni ricezione MQTT |
| FV / Batteria | 5 min (futuro) | ad ogni ricezione MQTT |
| Wallbox | 1 min quando attiva (futuro) | per sessione |
| Bollette Gmail | ogni 6 ore (futuro) | al ricevimento |
| Telecamere — motion check | continuo (frame delta) | solo su evento |
| Telecamere — AI inferenza | su motion trigger | evento + JPEG su NAS |

---

## Riferimenti utili

- Viessmann Developer Portal: https://developer.viessmann-climatesolutions.com
- Viessmann API base: `https://api.viessmann-climatesolutions.com/iot/v2`
- Huawei FusionSolar API: https://eu5.fusionsolar.huawei.com/unisso/login
- MQTTnet (C#): https://github.com/dotnet/MQTTnet
- Google Gmail API .NET: https://developers.google.com/gmail/api/quickstart/dotnet
- Blazor WASM PWA: https://learn.microsoft.com/en-us/aspnet/core/blazor/progressive-web-app
- Cloudflare Tunnel: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/
- YOLOv8 Nano ONNX: https://github.com/ultralytics/ultralytics
- ONNX Runtime Python: https://onnxruntime.ai/docs/get-started/with-python.html
- Reolink RTSP URL format: `rtsp://{user}:{pass}@{ip}:554/h264Preview_01_main`
- HLS.js (player browser): https://github.com/video-dev/hls.js/

---

*Documento aggiornato con Claude — giugno 2026*
