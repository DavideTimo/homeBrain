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
CasaTimo/
├── CasaTimo.sln
├── docker-compose.yml
├── .env                          # secrets (non committare)
│
├── src/
│   ├── CasaTimo.Web/             # Blazor WebAssembly (frontend)
│   ├── CasaTimo.Api/             # ASP.NET Core (API + SignalR)
│   ├── CasaTimo.Workers/         # Background services
│   │   ├── ViessmannConnector    # polling PDC + VMC → MQTT
│   │   ├── HuaweiConnector       # polling FV + batteria → MQTT
│   │   ├── DaikinConnector       # polling clima → MQTT
│   │   ├── WallboxConnector      # OCPP → MQTT
│   │   ├── HistoryRecorder       # MQTT → SQLite ogni ora
│   │   └── BillWatcher           # Gmail → PDF → DB + NAS
│   ├── CasaTimo.Core/            # Modelli condivisi, interfacce
│   └── CasaTimo.Infrastructure/  # DB context, NAS client, MQTT client
│
└── tests/
    └── CasaTimo.Tests/
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
                (ogni ora → SQLite NAS)     (sicurezza, AI, ecc.)
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

### STEP 1 — Scaffolding del progetto ✅ da fare
**Obiettivo:** creare la struttura base del progetto funzionante in locale.

**Prompt suggerito per vibe coding:**
> "Crea una solution .NET 9 chiamata CasaTimo con i seguenti progetti: CasaTimo.Web (Blazor WebAssembly), CasaTimo.Api (ASP.NET Core Minimal API), CasaTimo.Core (class library), CasaTimo.Infrastructure (class library), CasaTimo.Workers (Worker Service). Aggiungi un docker-compose.yml con Mosquitto MQTT broker. La Web chiama l'Api, l'Api usa Core e Infrastructure."

**Output atteso:**
- Solution con 5 progetti che compilano
- docker-compose con Mosquitto
- Blazor con una pagina Index placeholder
- API con un endpoint `/health` che risponde 200

---

### STEP 2 — Modelli dati (Core) ✅ da fare
**Obiettivo:** definire i modelli C# condivisi tra tutti i servizi.

**Entità da creare in `CasaTimo.Core/Models/`:**

```csharp
// Dati impianti (time-series)
SensorReading { Id, DeviceId, Metric, Value, Unit, Timestamp }
Device { Id, Name, Type, Location, IsActive }

// Bollette e spese
Bill { Id, Type, Issuer, Amount, DueDate, PeriodFrom, PeriodTo, 
       PdfPath, EmailId, CreatedAt, IsPaid }
BillType { Electricity, Water, Tari, Maintenance, Other }
Reminder { Id, BillId, DueDate, DaysBefore, IsSent, Message }

// Manutenzioni
MaintenanceRecord { Id, DeviceId, Description, Date, Cost, NextDueDate }
```

**Prompt suggerito:**
> "In CasaTimo.Core crea i modelli dati come da specifica. Aggiungi in CasaTimo.Infrastructure il DbContext EF Core con SQLite, con le migrations per tutte le entità. Aggiungi il NuGet Microsoft.EntityFrameworkCore.Sqlite."

---

### STEP 3 — MQTT infrastructure ✅ da fare
**Obiettivo:** client MQTT condiviso e topic conventions.

**Topic conventions da adottare:**
```
casatimo/pdc/temperature/supply      # temp mandata PDC
casatimo/pdc/temperature/return      # temp ritorno
casatimo/pdc/power/consumption       # consumo kW
casatimo/pdc/dhw/temperature         # acqua calda sanitaria
casatimo/fv/power/production         # produzione FV kW
casatimo/fv/battery/soc              # state of charge %
casatimo/fv/battery/power            # flusso batteria kW
casatimo/fv/grid/export              # energia in rete kW
casatimo/wallbox/power               # potenza ricarica kW
casatimo/wallbox/session/energy      # energia sessione kWh
casatimo/daikin/zone/{n}/temperature # temp zona n
```

**Prompt suggerito:**
> "In CasaTimo.Infrastructure crea un MqttClientService che usa MQTTnet. Deve permettere di pubblicare e sottoscrivere a topic. Aggiungilo come singleton in DI. Usa le credenziali da configurazione (appsettings / .env)."

---

### STEP 4 — Connettore Viessmann (PDC + VMC) ✅ da fare
**Obiettivo:** leggere i dati reali dalla pompa di calore e pubblicarli su MQTT.

**Dati da leggere:**
- Temperatura mandata / ritorno
- Temperatura acqua calda sanitaria
- Potenza consumata
- Modalità operativa (riscaldamento / raffrescamento / ACS / standby)
- COP istantaneo
- Temperatura esterna

**Dipendenza:** libreria Python `PyViCare` oppure implementazione diretta OAuth2 in C# chiamando `api.viessmann.com`.

**Nota:** valutare se usare un sidecar Python con PyViCare che pubblica su MQTT, lasciando C# solo come consumer. Più semplice e robusto.

**Prompt suggerito:**
> "Crea in CasaTimo.Workers un ViessmannConnector come BackgroundService C#. Ogni 5 minuti chiama le API Viessmann (OAuth2, client_id e client_secret da configurazione), legge le metriche della Vitocal 222-S e pubblica su MQTT sui topic casatimo/pdc/*. Gestisci il refresh del token automaticamente."

---

### STEP 5 — Connettore Huawei FusionSolar ✅ da fare
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
> "Crea HuaweiConnector come BackgroundService. Usa l'API REST di Huawei FusionSolar (endpoint /thirdData/getStationRealKpi). Autentica con username/password, gestisci la sessione con cookie. Pubblica su MQTT i topic casatimo/fv/*."

---

### STEP 6 — HistoryRecorder (storage orario su NAS) ✅ da fare
**Obiettivo:** sottoscriversi ai topic MQTT e salvare un campione orario su SQLite.

**Logica:**
- Si abbona a `casatimo/#`
- Mantiene in memoria l'ultimo valore per ogni topic
- Ogni ora scrive una riga per ogni metrica nel DB time-series
- Il file SQLite sta su path configurabile (mount NAS)

**Prompt suggerito:**
> "Crea HistoryRecorder come BackgroundService. Si sottoscrive a tutti i topic MQTT casatimo/#, mantiene un dizionario in memoria con l'ultimo valore per topic, e ogni ora scrive su SQLite (EF Core) una SensorReading per ogni metrica. Il path del DB è configurabile per puntare al NAS montato."

---

### STEP 7 — BillWatcher (Gmail → PDF → DB) ✅ da fare
**Obiettivo:** leggere automaticamente Gmail, riconoscere bollette, estrarre dati, salvare.

**Logica:**
- Ogni 6 ore controlla Gmail (API Google, OAuth2)
- Cerca email da mittenti noti (configurabili): gestore luce, acqua, TARI
- Scarica i PDF allegati
- Usa Claude API (o regex su testo estratto) per estrarre: importo, scadenza, periodo, consumi
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
> "Crea BillWatcher come BackgroundService. Usa Google.Apis.Gmail.v1 per leggere le email. Filtra per mittenti configurati. Scarica PDF allegati. Usa iTextSharp o PdfPium per estrarre il testo. Chiama Claude API (claude-sonnet-4-20250514) passando il testo del PDF per estrarre in JSON: importo, scadenza, periodo_da, periodo_a, consumi_kwh. Salva tutto su SQLite e PDF su path NAS."

---

### STEP 8 — API endpoints ✅ da fare
**Obiettivo:** esporre tutti i dati al frontend via REST e WebSocket.

**Endpoints da creare:**

```
GET  /api/devices                    # lista dispositivi
GET  /api/sensors/live               # ultimo valore tutti i sensori
GET  /api/sensors/history?from=&to=  # storico time-series
WS   /ws/live                        # stream real-time via SignalR

GET  /api/bills                      # lista bollette
GET  /api/bills/{id}                 # dettaglio bolletta
GET  /api/bills/{id}/pdf             # scarica PDF
POST /api/bills/{id}/paid            # segna come pagata

GET  /api/reminders                  # reminder attivi
PUT  /api/reminders/{id}/dismiss     # dismetti reminder

GET  /api/maintenance                # storico manutenzioni
POST /api/maintenance                # aggiungi manutenzione
```

---

### STEP 9 — Dashboard domotica (Blazor) ✅ da fare
**Obiettivo:** pagina principale con dati real-time degli impianti.

**Componenti da creare:**
- `EnergyFlowCard` — flusso FV → batteria → casa → rete (animato)
- `BatteryCard` — SOC con barra e trend
- `HeatPumpCard` — temperature, modalità, COP
- `WallboxCard` — sessione ricarica attiva
- `ProductionChart` — grafico produzione FV giornaliera (Recharts / ApexCharts)
- `LiveIndicator` — pallino verde pulsante quando dati aggiornati

**Connessione real-time:** SignalR hub che riceve dati MQTT e li pusha al browser.

---

### STEP 10 — Sezione bollette e spese (Blazor) ✅ da fare
**Obiettivo:** pagina gestione spese domestiche.

**Componenti da creare:**
- `BillList` — tabella bollette con filtri (anno, tipo, pagate/non pagate)
- `BillDetail` — dettaglio con link al PDF
- `SpendingChart` — grafico spese mensili per categoria (luce, acqua, TARI)
- `ReminderBanner` — banner in alto se ci sono scadenze nei prossimi 7 giorni
- `MaintenanceTimeline` — timeline manutenzioni impianti passate e future

---

### STEP 11 — PWA e accesso remoto ✅ da fare
**Obiettivo:** rendere l'app installabile su mobile e accessibile da remoto.

**Azioni:**
- Configurare `manifest.json` in Blazor WASM (icona, nome, colori)
- Aggiungere service worker per offline basic
- Configurare Cloudflare Tunnel sul mini PC
- Dominio personalizzato (es. `casa.timo.dev`) via Cloudflare

---

### STEP 12 — Reminder e notifiche ✅ da fare
**Obiettivo:** notifiche push su mobile per scadenze.

**Opzioni:**
- Web Push API (gratuito, nativo browser) — notifiche push anche con app chiusa
- Telegram bot — alternativa semplice, nessuna configurazione
- Email — fallback sempre disponibile

---

## Note architetturali importanti

### Sicurezza
- Tutti i secret in `.env` (mai nel codice)
- MQTT con autenticazione username/password
- API con JWT authentication
- Cloudflare Tunnel: nessuna porta aperta sul router
- HTTPS ovunque (Cloudflare gestisce il certificato)

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
| FV / Batteria | 5 min | 1 ora |
| Pompa di calore | 5 min | 1 ora |
| Wallbox | 1 min (quando attiva) | per sessione |
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

*Documento generato con Claude — aggiornare a ogni step completato*
