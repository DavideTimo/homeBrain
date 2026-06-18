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
| Pompa di calore | Viessmann Vitocal 222-S | Viessmann API (OAuth2) via PyViCare |
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
| API | ASP.NET Core Minimal API | REST + WebSocket + SignalR |
| Worker services | C# Background Services | Connettori e BillWatcher |
| Message broker | Mosquitto (MQTT) | Bus condiviso sensori |
| Database domotica | SQLite (time-series) | Storage orario su NAS |
| Database bollette | SQLite | Importi, scadenze, metadati PDF |
| Storage PDF | NAS Synology (riconfigurare) | Cartelle per anno/tipo |
| Hosting | Mini PC locale (Beelink N305 o simile) | Docker Compose |
| Accesso remoto | Cloudflare Tunnel | Zero config router |

---

## Struttura del progetto

```
homeBrain/
├── CasaTimo.sln
├── docker-compose.yml
├── .env.example                      # template variabili d'ambiente (committato)
├── .env                              # secrets reali (NON committare)
│
└── src/
    ├── CasaTimo.Web/                 # Blazor WebAssembly (frontend)
    │   └── Pages/
    │       ├── Connectors.razor      # admin UI per configurare i connettori
    │       └── ...
    ├── CasaTimo.Api/                 # ASP.NET Core Minimal API
    │   ├── Program.cs                # endpoint REST + JWT auth
    │   ├── appsettings.json
    │   └── appsettings.Development.json
    ├── CasaTimo.Workers/             # Background services
    │   ├── ViessmannConnector        # polling PDC + VMC → MQTT (scaffold)
    │   ├── HistoryRecorder           # MQTT casatimo/# → SQLite
    │   └── Worker                   # health-check heartbeat
    ├── CasaTimo.Core/                # Modelli condivisi, interfacce
    │   └── Models/                  # SensorReading, Device, Bill, ...
    ├── CasaTimo.Infrastructure/      # DB context, MQTT client, connettori
    │   ├── Data/CasaTimoDbContext    # EF Core + SQLite
    │   ├── Messaging/MqttClientService
    │   └── Connectors/ViessmannConnector
    └── CasaTimo.Api.Tests/           # Test di integrazione xUnit
```

---

## Architettura — flusso dati

```
[Impianti fisici]
      │ API polling (ogni 5 min real-time)
      ▼
[Worker Services C#]  ──────────────────→  [MQTT Broker — Mosquitto]
                                                    │
                        ┌───────────────────────────┤
                        │                           │
                [HistoryRecorder]           [Altri servizi futuri]
                (realtime → SQLite)         (sicurezza, AI, ecc.)
                        │
                        ▼
[Gmail] → [BillWatcher] → [SQLite bollette] + [PDF su NAS]
                        │
                        ▼
              [ASP.NET Core API]
              REST + WebSocket + SignalR
                        │
                        ▼
              [Blazor WebAssembly]
              Dashboard · Bollette · Reminder
              (PWA su mobile)
```

---

## Step di sviluppo

### STEP 1 — Scaffolding del progetto ✅ Completato
Struttura solution con 5 progetti (`Core`, `Infrastructure`, `Api`, `Workers`, `Web`), docker-compose con Mosquitto, pagina placeholder Blazor, endpoint `/health`.

---

### STEP 2 — Modelli dati (Core) ✅ Completato
Entità definite in `CasaTimo.Core/Models/`:

```csharp
SensorReading { Id, DeviceId, Metric, Value, Unit, Timestamp }
Device { Id, Name, Type, Location, IsActive }
Bill { Id, Type, Issuer, Amount, DueDate, PeriodFrom, PeriodTo,
       PdfPath, EmailId, CreatedAt, IsPaid }
BillType { Electricity, Water, Tari, Maintenance, Other }
Reminder { Id, BillId, DueDate, DaysBefore, IsSent }
MaintenanceRecord { Id, DeviceId, Description, Date, Cost, NextDueDate }
ConnectorConfig { Id, ConnectorName, SettingsJson, UpdatedAt }
```

`CasaTimoDbContext` (EF Core + SQLite) con indici su `SensorReading(DeviceId, Timestamp)` e unique su `Bill.EmailId`.

---

### STEP 3 — MQTT infrastructure ✅ Completato
`MqttClientService` (MQTTnet) registrato come singleton con supporto publish, subscribe ed evento `MessageReceived` per i consumer interni.

**Topic conventions adottate:**
```
casatimo/{deviceId}/{metric}

casatimo/pdc/temperature_supply      # temp mandata PDC
casatimo/pdc/temperature_return      # temp ritorno
casatimo/pdc/power_consumption       # consumo kW
casatimo/pdc/dhw_temperature         # acqua calda sanitaria
casatimo/fv/power_production         # produzione FV kW
casatimo/fv/battery_soc              # state of charge %
casatimo/fv/battery_power            # flusso batteria kW
casatimo/fv/grid_export              # energia in rete kW
casatimo/wallbox/power               # potenza ricarica kW
casatimo/wallbox/session_energy      # energia sessione kWh
casatimo/daikin/zone_{n}_temperature # temp zona n
```

**Payload:** JSON `{"value": 42.5, "unit": "kW"}` oppure stringa numerica plain.

---

### STEP 4 — Connettore Viessmann (PDC + VMC) 🔄 Parziale (scaffold)
`ViessmannConnector` implementato come `BackgroundService`: gestisce OAuth2 (sia `client_credentials` che `password` grant), polling configurabile, pubblica su MQTT. Mancano le chiamate alle metriche specifiche della Vitocal 222-S (endpoint da integrare).

**Credenziali:** da `.env` / variabili d'ambiente (`CASATIMO_WORKERS__Viessmann__*`).

**Prompt suggerito per completare:**
> "In ViessmannConnector, dopo aver ottenuto il token, chiama le API Viessmann per leggere temperature mandata/ritorno, temperatura ACS, potenza consumata e COP della Vitocal 222-S. Pubblica ogni metrica su MQTT con topic casatimo/pdc/{metric}."

---

### STEP 5 — Connettore Huawei FusionSolar ⬜ Da fare
**Obiettivo:** leggere produzione FV, stato batteria, flussi energetici.

**Dati da leggere:**
- Produzione istantanea FV (kW)
- Energia prodotta oggi (kWh)
- State of charge batteria (%)
- Potenza batteria (carica/scarica kW)
- Potenza esportata in rete (kW)
- Autoconsumo (%)

**Nota:** l'API Huawei FusionSolar richiede registrazione su `eu5.fusionsolar.huawei.com`. Credenziali da `.env`.

**Prompt suggerito:**
> "Crea HuaweiConnector come BackgroundService, seguendo lo stesso pattern di ViessmannConnector. Usa l'API REST di Huawei FusionSolar (endpoint /thirdData/getStationRealKpi). Autentica con username/password, gestisci la sessione con cookie. Pubblica su MQTT i topic casatimo/fv/*."

---

### STEP 6 — HistoryRecorder (storage su SQLite) ✅ Completato
`HistoryRecorder` implementato come `BackgroundService`:
- Si abbona a `casatimo/#` via `MqttClientService.MessageReceived`
- Parsa il topic `casatimo/{deviceId}/{metric}` e il payload JSON/numerico
- Salva immediatamente ogni lettura come `SensorReading` su SQLite (via `IServiceScopeFactory`)

---

### STEP 7 — BillWatcher (Gmail → PDF → DB) ⬜ Da fare
**Obiettivo:** leggere automaticamente Gmail, riconoscere bollette, estrarre dati, salvare.

**Logica:**
- Ogni 6 ore controlla Gmail (API Google, OAuth2)
- Cerca email da mittenti noti (configurabili): gestore luce, acqua, TARI
- Scarica i PDF allegati
- Usa Claude API per estrarre: importo, scadenza, periodo, consumi
- Salva il PDF su NAS in `/bollette/{anno}/{tipo}/`
- Salva i metadati su SQLite (tabella Bills)
- Crea un Reminder 7 giorni prima della scadenza

**Mittenti da configurare:**
```json
"BillWatchers": [
  { "Sender": "@enel.it", "Type": "Electricity" },
  { "Sender": "@hera.it", "Type": "Water" },
  { "Sender": "comune", "Type": "Tari" }
]
```

**Prompt suggerito:**
> "Crea BillWatcher come BackgroundService. Usa Google.Apis.Gmail.v1 per leggere le email. Filtra per mittenti configurati. Scarica PDF allegati. Usa iTextSharp o PdfPium per estrarre il testo. Chiama Claude API (claude-sonnet-4-6) passando il testo del PDF per estrarre in JSON: importo, scadenza, periodo_da, periodo_a, consumi_kwh. Salva tutto su SQLite e PDF su path NAS."

---

### STEP 8 — API endpoints ✅ Parziale
**Implementati:**
```
GET  /                               # info versione
GET  /health                         # stato servizio
POST /api/auth/token                 # login → JWT Bearer token
GET  /api/connectors                 # lista configurazioni connettori
GET  /api/connectors/{name}          # singola configurazione
PUT  /api/connectors/{name}          # aggiorna configurazione (richiede JWT)
```

**Da aggiungere:**
```
GET  /api/devices
GET  /api/sensors/live
GET  /api/sensors/history?from=&to=
WS   /ws/live                        # SignalR real-time
GET  /api/bills  (+ /pdf, /paid)
GET  /api/reminders
GET  /api/maintenance
POST /api/maintenance
```

---

### STEP 9 — Dashboard domotica (Blazor) ⬜ Da fare
**Componenti da creare:**
- `EnergyFlowCard` — flusso FV → batteria → casa → rete (animato)
- `BatteryCard` — SOC con barra e trend
- `HeatPumpCard` — temperature, modalità, COP
- `WallboxCard` — sessione ricarica attiva
- `ProductionChart` — grafico produzione FV giornaliera (ApexCharts)
- `LiveIndicator` — pallino verde pulsante quando dati aggiornati

**Connessione real-time:** SignalR hub che riceve dati MQTT e li pusha al browser.

---

### STEP 10 — Sezione bollette e spese (Blazor) ⬜ Da fare
**Componenti da creare:**
- `BillList` — tabella bollette con filtri (anno, tipo, pagate/non pagate)
- `BillDetail` — dettaglio con link al PDF
- `SpendingChart` — grafico spese mensili per categoria (luce, acqua, TARI)
- `ReminderBanner` — banner in alto se ci sono scadenze nei prossimi 7 giorni
- `MaintenanceTimeline` — timeline manutenzioni impianti passate e future

---

### STEP 11 — PWA e accesso remoto ⬜ Da fare
**Azioni:**
- Configurare `manifest.json` in Blazor WASM (icona, nome, colori)
- Aggiungere service worker per offline basic
- Configurare Cloudflare Tunnel sul mini PC
- Dominio personalizzato (es. `casa.timo.dev`) via Cloudflare

---

### STEP 12 — Reminder e notifiche ⬜ Da fare
**Opzioni:**
- Web Push API (gratuito, nativo browser) — notifiche push anche con app chiusa
- Telegram bot — alternativa semplice, nessuna configurazione
- Email — fallback sempre disponibile

---

## Note architetturali importanti

### Sicurezza
- Tutti i secret in `.env` (mai nel codice) — vedi `.env.example` per la lista completa
- Autenticazione JWT Bearer su tutti gli endpoint di scrittura (`POST /api/auth/token` → Bearer token)
- MQTT con autenticazione username/password (opzionale, da configurare su Mosquitto)
- CORS ristretto alle origini configurate in `appsettings.json` (`AllowedOrigins`)
- Cloudflare Tunnel: nessuna porta aperta sul router
- HTTPS ovunque (Cloudflare gestisce il certificato)
- Segreti di sviluppo via `dotnet user-secrets` (Workers ha già `UserSecretsId` configurato)

### Configurazione credenziali
Le variabili d'ambiente seguono la convenzione .NET con doppio underscore (`__`):
```bash
# esempio (vedi .env.example per la lista completa)
CASATIMO_API__AdminPassword=...
CASATIMO_API__Jwt__Key=...           # min 32 caratteri
CASATIMO_WORKERS__Viessmann__ClientId=...
```

In sviluppo locale usare `dotnet user-secrets`:
```bash
cd src/CasaTimo.Api
dotnet user-secrets set "AdminPassword" "la_tua_password"
dotnet user-secrets set "Jwt:Key" "chiave_lunga_almeno_32_caratteri"
```

### NAS Synology
- Il DS115j attuale **non è compatibile** (ARM 32bit, no Docker)
- Usarlo solo come storage di file (PDF bollette, SQLite backup)
- Montarlo come share SMB sul mini PC: `/mnt/nas/casatimo/`
- Valutare upgrade NAS in futuro per funzionalità più avanzate

### Espandibilità futura
- Nuovi sensori (ESP32 + ESPHome) → pubblicano direttamente su MQTT
- Telecamere → Frigate pubblica eventi su MQTT → servizio sicurezza li consuma
- LLM locale (Ollama) → chiamato da BillWatcher o da automazioni custom
- Attuatori → API pubblica comandi MQTT → worker li esegue

### Frequenze di aggiornamento
| Dato | Frequenza live | Frequenza storage |
|---|---|---|
| FV / Batteria | 5 min | ad ogni ricezione MQTT |
| Pompa di calore | 5 min | ad ogni ricezione MQTT |
| Wallbox | 1 min (quando attiva) | ad ogni ricezione MQTT |
| Bollette Gmail | ogni 6 ore | al ricevimento |

---

## Riferimenti utili

- Viessmann API: https://developer.viessmann.com
- Huawei FusionSolar API: https://eu5.fusionsolar.huawei.com/unisso/login
- PyViCare (libreria Python): https://github.com/openvicare/PyViCare
- MQTTnet (C#): https://github.com/dotnet/MQTTnet
- Google Gmail API .NET: https://developers.google.com/gmail/api/quickstart/dotnet
- Blazor WASM PWA: https://learn.microsoft.com/en-us/aspnet/core/blazor/progressive-web-app
- Cloudflare Tunnel: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/

---

*Documento aggiornato con Claude — giugno 2026*
