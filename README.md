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
| Message broker | Mosquitto 2.0 (Docker) | Bus condiviso sensori |
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
├── mosquitto/
│   └── config/mosquitto.conf         # listener 1883, allow_anonymous true
├── sidecar-viessmann/                # sidecar Python Vitocal 222-S
│   ├── main.py                       # polling loop → MQTT
│   ├── setup_token.py                # autenticazione one-time (PKCE)
│   ├── requirements.txt
│   ├── Dockerfile
│   └── data/viessmann_token.json     # token OAuth2 (NON committare)
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
[sidecar-viessmann Python]  ──→  [MQTT Broker — Mosquitto :1883]
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
# 1. Broker MQTT
docker-compose up -d mosquitto

# 2. API backend (terminale 1)
dotnet run --project src/CasaTimo.Api

# 3. Workers / HistoryRecorder (terminale 2)
dotnet run --project src/CasaTimo.Workers

# 4. Frontend Blazor (terminale 3)
dotnet run --project src/CasaTimo.Web

# 5. Sidecar Viessmann — setup one-time (solo la prima volta)
cd sidecar-viessmann
python setup_token.py        # salva data/viessmann_token.json

# 5b. Sidecar Viessmann — polling continuo (terminale 4)
set VIESSMANN_USER=davide.timo1982@libero.it
set VIESSMANN_CLIENT_ID=ff9de13f959927123b166ccfea88c624
python main.py
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
Mosquitto configurato con `mosquitto/config/mosquitto.conf` (listener 1883, allow_anonymous true).

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

---

### STEP 5 — Connettore Huawei FusionSolar ⬜ Da fare

**Dati da leggere:** produzione FV istantanea, energia oggi, SOC batteria, potenza batteria, export rete, autoconsumo.

**Prompt suggerito:**
> "Crea un sidecar Python `sidecar-huawei/main.py` seguendo il pattern di `sidecar-viessmann`. Autentica su `eu5.fusionsolar.huawei.com` con username/password (gestisci il cookie di sessione). Legge `/thirdData/getStationRealKpi` e pubblica su casatimo/fv/*."

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

## Note architetturali

### Sicurezza
- Tutti i secret in `.env` (mai nel codice) — vedi `.env.example`
- JWT Bearer auth su endpoint di scrittura
- `sidecar-viessmann/data/viessmann_token.json` escluso da git (`.gitignore`)
- Mosquitto: `allow_anonymous true` solo in sviluppo — aggiungere autenticazione in produzione
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

---

## Riferimenti utili

- Viessmann Developer Portal: https://developer.viessmann-climatesolutions.com
- Viessmann API base: `https://api.viessmann-climatesolutions.com/iot/v2`
- Huawei FusionSolar API: https://eu5.fusionsolar.huawei.com/unisso/login
- MQTTnet (C#): https://github.com/dotnet/MQTTnet
- Mosquitto Docker: https://hub.docker.com/_/eclipse-mosquitto
- Google Gmail API .NET: https://developers.google.com/gmail/api/quickstart/dotnet
- Blazor WASM PWA: https://learn.microsoft.com/en-us/aspnet/core/blazor/progressive-web-app
- Cloudflare Tunnel: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/

---

*Documento aggiornato con Claude — giugno 2026*
