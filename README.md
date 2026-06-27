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

---

## Impianti e dispositivi presenti

| Dispositivo | Modello | Protocollo / API |
|---|---|---|
| Pompa di calore | Viessmann Vitocal 222-S | API REST `api.viessmann-climatesolutions.com` |
| Fotovoltaico + Batteria | Huawei + LUNA 2000 (20 kWh) | Huawei FusionSolar API |
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
docker compose up huawei-sidecar        # quando disponibile
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

### STEP 5 — Connettore Huawei FusionSolar ✅ Completato (sidecar Python)

Sidecar Python `sidecar-huawei/` che autentica sulla northbound API FusionSolar e pubblica su MQTT.

**Autenticazione:** POST `/thirdData/login` con `userName` + `systemCode` (cookie di sessione `roarand`). Re-login automatico su failCode 401.

**Discovery on startup:** `getStationList` → stationCode, `getDevList` → device ID per tipo (inverter=1, batteria=39, meter=47).

**Poll loop (default 300s):**
- `getStationRealKpi` → `energy_today`
- `getDevRealKpi` inverter → `power_active`, `load_power`
- `getDevRealKpi` batteria → `battery_soc`, `battery_power`
- `getDevRealKpi` grid meter → `grid_power`

**Topic MQTT:**
```
casatimo/fv/power_active     kW   produzione FV istantanea
casatimo/fv/energy_today     kWh  energia prodotta oggi
casatimo/fv/battery_soc      %    SOC batteria LUNA 2000
casatimo/fv/battery_power    kW   potenza batteria (+ carica, - scarica)
casatimo/fv/grid_power       kW   potenza rete (+ export, - import)
casatimo/fv/load_power       kW   consumo casa istantaneo
```

**Approccio: Modbus TCP locale** (non cloud API)
- L'API northbound FusionSolar richiede un account installer — non disponibile per utenti finali
- Alternativa: `python-huawei-solar` si connette direttamente all'inverter sulla LAN via Modbus TCP (porta 6607), senza cloud né credenziali speciali
- Prerequisiti: IP dell'inverter sulla rete locale + Modbus TCP abilitato dall'app FusionSolar (`Dispositivi` → `Impostazioni` → `Configurazione comunicazione`)
- Dati ogni 30s invece di 300s (nessun rate limit cloud)

**Credenziali richieste in `.env`:**
- `HUAWEI_INVERTER_HOST` — IP locale dell'inverter (es. `192.168.1.100`)
- `HUAWEI_INVERTER_PORT` — porta Modbus (default `6607`)

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
- Protocollo **RTSP nativo** (ONVIF compatibile) — necessario per OpenCV
- Evitare telecamere cloud-only (Tuya, Wyze senza hack, ecc.)
- **Consigliato: Reolink**
  - Interno: E1 Pro o E1 Outdoor (WiFi 2K, ~35€)
  - Esterno: RLC-510A o RLC-810A (PoE, IP66, un cavo per dati + alimentazione)
  - Per le esterne preferire PoE: serve uno switch PoE (~30€)
- Risoluzione ottimale per l'AI: **1080p/2K** (il modello YOLO ridimensiona a 640×640 internamente, il 4K spreca CPU senza migliorare l'accuracy)

**Architettura:**
```
Telecamere (RTSP)
      │
      ▼
sidecar-camera/ (Python, una istanza per telecamera o multi-camera)
  ├── OpenCV → cattura frame via RTSP
  ├── Frame delta leggero → motion trigger (CPU quasi zero)
  ├── YOLOv8n ONNX → inferenza su frame con motion (~10-15 FPS su CPU mini PC)
  ├── Salva JPEG su NAS a 2 FPS durante motion event
  └── Pubblica MQTT:
        casatimo/cameras/{id}/motion   {"active": true/false}
        casatimo/cameras/{id}/person   {"confidence": 0.87, "count": 1}
      │
      ├── FFmpeg → HLS → Blazor (live view, latenza ~3-5s)
      └── MQTT → HistoryRecorder → SQLite (log eventi)
```

**AI: YOLOv8 Nano ONNX**
- Modello pre-addestrato COCO (persone, auto, animali, 80 classi)
- Dimensione: ~6MB, inference su CPU: ~50-100ms a 640×640
- Libreria: `onnxruntime` (nessuna dipendenza GPU)
- Motion detection via frame differencing (leggero, CPU <1%) → trigger per inferenza YOLO
- YOLO conferma se il motion è causato da persona/veicolo/animale, riducendo falsi positivi

**Registrazione:**
- Solo su motion event confermato (no registrazione continua)
- 1 frame ogni 500ms (2 FPS) salvato come JPEG su NAS Synology (`/mnt/nas/casatimo/cameras/{id}/YYYY-MM-DD/`)
- Retention configurabile (es. 30 giorni, poi auto-delete)
- Evento loggato su SQLite con timestamp, camera ID, tipo oggetto rilevato, confidence

**Live view in Blazor:**
- FFmpeg transcoding RTSP → HLS (segmenti .ts ogni 2s)
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
- Implementazione suggerita: una istanza Docker per camera (scalabilità, isolamento crash)

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
