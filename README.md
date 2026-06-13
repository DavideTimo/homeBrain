# Casa Timò — Home Management Platform

Piattaforma domotica unificata per gestire impianti di casa (fotovoltaico, pompa di calore, wallbox), bollette e spese domestiche, con dashboard real-time e accesso remoto via PWA.

---

## Indice

- [Architettura](#architettura)
- [Struttura del progetto](#struttura-del-progetto)
- [Requisiti](#requisiti)
- [Setup rapido](#setup-rapido)
- [Variabili d'ambiente](#variabili-dambiente)
- [MQTT — topic conventions](#mqtt--topic-conventions)
- [API REST](#api-rest)
- [Worker services](#worker-services)
- [Database](#database)
- [Docker Compose](#docker-compose)
- [Accesso remoto](#accesso-remoto)
- [Espandibilità](#espandibilità)

---

## Architettura

```
[Impianti fisici]
      │ polling ogni 5 min
      ▼
[Worker Services]  ──────────────────→  [Mosquitto MQTT]
  ViessmannConnector (PDC + VMC)                │
  HuaweiConnector (FV + Batteria)      ┌────────┤
  WallboxConnector (OCPP)              │        │
  DaikinConnector (Clima)      [HistoryRecorder] [SensorBroadcastService]
                                (→ SQLite/ora)   (→ SignalR/browser)
[Gmail] → [BillWatcher] → [SQLite bollette] + [PDF su NAS]

                    [ASP.NET Core Minimal API]
                    REST · SignalR · Swagger
                              │
                    [Blazor WebAssembly PWA]
                    Dashboard · Bollette · Manutenzioni
```

---

## Struttura del progetto

```
CasaTimo/
├── CasaTimo.sln
├── docker-compose.yml
├── .env                          ← secrets (non committare mai)
├── .env.example                  ← template da copiare
├── mosquitto/
│   └── config/
│       ├── mosquitto.conf
│       └── passwd                ← generare con mosquitto_passwd
│
├── src/
│   ├── CasaTimo.Core/            ← modelli e interfacce condivisi
│   │   ├── Models/
│   │   │   ├── SensorReading.cs
│   │   │   ├── Device.cs
│   │   │   ├── Bill.cs
│   │   │   ├── Reminder.cs
│   │   │   ├── MaintenanceRecord.cs
│   │   │   └── MqttTopics.cs     ← costanti topic MQTT
│   │   └── Interfaces/
│   │       └── IMqttService.cs
│   │
│   ├── CasaTimo.Infrastructure/  ← DB context, MQTT client, NAS
│   │   ├── Data/
│   │   │   └── CasaTimoDbContext.cs
│   │   ├── Mqtt/
│   │   │   └── MqttClientService.cs
│   │   └── Extensions/
│   │       └── ServiceCollectionExtensions.cs
│   │
│   ├── CasaTimo.Api/             ← ASP.NET Core Minimal API
│   │   ├── Program.cs            ← endpoints REST + DI setup
│   │   ├── Hubs/
│   │   │   └── SensorHub.cs      ← SignalR hub + broadcast service
│   │   ├── appsettings.json
│   │   └── Dockerfile
│   │
│   ├── CasaTimo.Workers/         ← background services
│   │   ├── Program.cs
│   │   ├── ViessmannConnector.cs ← Vitocal 222-S → MQTT
│   │   ├── HuaweiConnector.cs    ← FusionSolar → MQTT
│   │   ├── HistoryRecorder.cs    ← MQTT → SQLite ogni ora
│   │   ├── BillWatcher.cs        ← Gmail → PDF → Claude → DB
│   │   └── Dockerfile
│   │
│   └── CasaTimo.Web/             ← Blazor WebAssembly PWA
│       ├── Pages/
│       │   ├── Home.razor        ← dashboard real-time
│       │   ├── Bills.razor       ← gestione bollette
│       │   └── Maintenance.razor ← storico manutenzioni
│       ├── Services/
│       │   ├── SensorService.cs  ← SignalR client
│       │   ├── BillService.cs    ← HTTP client bollette
│       │   └── ReminderService.cs
│       └── wwwroot/
│           ├── manifest.json     ← PWA manifest
│           └── service-worker.js
│
└── tests/
    └── CasaTimo.Tests/
```

---

## Requisiti

| Software | Versione minima | Note |
|---|---|---|
| .NET SDK | 8.0 | `dotnet --version` |
| Docker + Compose | 24+ | per il deploy locale |
| Mini PC (host) | Beelink N305 o simile | 8GB RAM raccomandati |
| NAS Synology | DS115j o superiore | solo come storage SMB |

---

## Setup rapido

### 1. Clona e configura i secrets

```bash
git clone https://github.com/DavideTimo/homeBrain.git
cd homeBrain
cp .env.example .env
nano .env    # inserisci le credenziali reali
```

### 2. Crea il file password Mosquitto

```bash
mkdir -p mosquitto/data mosquitto/log
docker run --rm -v $(pwd)/mosquitto/config:/mosquitto/config \
  eclipse-mosquitto:2 \
  mosquitto_passwd -c /mosquitto/config/passwd casatimo
# inserisci la password quando richiesto — deve corrispondere a MQTT_PASS nel .env
```

### 3. Monta il NAS (opzionale ma raccomandato)

```bash
# Su Ubuntu/Debian, aggiungi a /etc/fstab:
# //192.168.1.x/casatimo  /mnt/nas/casatimo  cifs  credentials=/etc/nas-creds,uid=1000,gid=1000  0  0
sudo mkdir -p /mnt/nas/casatimo
sudo mount -a
```

### 4. Avvia con Docker Compose

```bash
docker compose up --build -d
docker compose logs -f   # monitora i log
```

### 5. Verifica

```
http://localhost:5000/health       → {"status":"ok","time":"..."}
http://localhost:5000/swagger      → Swagger UI con tutti gli endpoint
http://localhost:5001              → Blazor frontend (se avviato separato)
```

### Sviluppo locale (senza Docker)

```bash
# Avvia Mosquitto in Docker
docker compose up mosquitto -d

# Terminale 1 — API
cd src/CasaTimo.Api
dotnet run

# Terminale 2 — Workers
cd src/CasaTimo.Workers
dotnet run

# Terminale 3 — Frontend
cd src/CasaTimo.Web
dotnet run
```

---

## Variabili d'ambiente

Tutte le variabili vanno nel file `.env` nella root del progetto (mai committare `.env`).

### Viessmann (Pompa di Calore + VMC)

| Variabile | Descrizione | Come ottenerla |
|---|---|---|
| `VIESSMANN_CLIENT_ID` | OAuth2 client ID | [developer.viessmann.com](https://developer.viessmann.com) → My Apps |
| `VIESSMANN_CLIENT_SECRET` | OAuth2 client secret | idem |

Il `ViessmannConnector` usa il flow `client_credentials` di OAuth2 verso `iam.viessmann.com` e interroga `api.viessmann.com/iot/v1`.

### Huawei FusionSolar (Fotovoltaico + Batteria)

| Variabile | Descrizione | Come ottenerla |
|---|---|---|
| `HUAWEI_FUSIONSOLAR_USER` | Username account FusionSolar | [eu5.fusionsolar.huawei.com](https://eu5.fusionsolar.huawei.com) → abilitare accesso API |
| `HUAWEI_FUSIONSOLAR_PASS` | Password account | idem |
| `HUAWEI_STATION_CODE` | Codice impianto | nel portale FusionSolar, colonna "Station Code" |

### MQTT (Mosquitto)

| Variabile | Default | Descrizione |
|---|---|---|
| `MQTT_HOST` | `mosquitto` | hostname del broker (nome servizio Docker) |
| `MQTT_PORT` | `1883` | porta TCP |
| `MQTT_USER` | `casatimo` | username (deve corrispondere a quello creato con `mosquitto_passwd`) |
| `MQTT_PASS` | — | password scelata durante la generazione del file passwd |

### Gmail / BillWatcher

| Variabile | Descrizione | Come ottenerla |
|---|---|---|
| `GOOGLE_CLIENT_ID` | OAuth2 client ID | [console.cloud.google.com](https://console.cloud.google.com) → API Gmail → Credenziali |
| `GOOGLE_CLIENT_SECRET` | OAuth2 client secret | idem |

Al primo avvio, `BillWatcher` aprirà un browser per il consenso OAuth2 e salverà il token localmente. Le autorizzazioni richieste sono solo `gmail.readonly`.

Per configurare i mittenti da monitorare, modifica `appsettings.json` in `CasaTimo.Api`:

```json
"BillWatchers": [
  { "Sender": "@enel.it",  "Type": "Electricity" },
  { "Sender": "@hera.it",  "Type": "Water" },
  { "Sender": "comune",    "Type": "Tari" },
  { "Sender": "@eni.it",   "Type": "Gas" }
]
```

### NAS / Storage

| Variabile | Default | Descrizione |
|---|---|---|
| `NAS_PDF_PATH` | `/mnt/nas/casatimo/bollette` | cartella dove salvare i PDF delle bollette, organizzati in `{anno}/{tipo}/` |
| `NAS_DB_PATH` | `/mnt/nas/casatimo/data` | cartella dove risiede il file SQLite (`casatimo.db`) |

Se il NAS non è disponibile, entrambi i path possono puntare a directory locali.

### Claude API (parsing bollette)

| Variabile | Descrizione |
|---|---|
| `ANTHROPIC_API_KEY` | Chiave API di Anthropic per l'estrazione dati dai PDF delle bollette |

Il `BillWatcher` usa il modello `claude-haiku-4-5` per estrarre da ogni PDF: importo, scadenza, periodo di competenza, consumi in kWh. Se la chiave non è configurata, il parsing viene saltato e le bollette non vengono salvate.

---

## MQTT — topic conventions

Tutti i messaggi usano payload stringa contenente un numero in formato invariant culture (es. `"3.14"`).

| Topic | Unità | Sorgente |
|---|---|---|
| `casatimo/pdc/temperature/supply` | °C | ViessmannConnector |
| `casatimo/pdc/temperature/return` | °C | ViessmannConnector |
| `casatimo/pdc/temperature/outdoor` | °C | ViessmannConnector |
| `casatimo/pdc/dhw/temperature` | °C | ViessmannConnector |
| `casatimo/pdc/power/consumption` | kW | ViessmannConnector |
| `casatimo/pdc/mode` | stringa | ViessmannConnector |
| `casatimo/fv/power/production` | kW | HuaweiConnector |
| `casatimo/fv/energy/today` | kWh | HuaweiConnector |
| `casatimo/fv/battery/soc` | % | HuaweiConnector |
| `casatimo/fv/battery/power` | kW (+ carica, - scarica) | HuaweiConnector |
| `casatimo/fv/grid/export` | kW | HuaweiConnector |
| `casatimo/wallbox/power` | kW | WallboxConnector |
| `casatimo/wallbox/session/energy` | kWh | WallboxConnector |
| `casatimo/wallbox/status` | stringa | WallboxConnector |
| `casatimo/daikin/zone/{n}/temperature` | °C | DaikinConnector |

Per aggiungere un nuovo sensore (es. ESP32 con ESPHome), è sufficiente che pubblichi su un topic `casatimo/{device}/{metric}` — verrà automaticamente raccolto da `HistoryRecorder` e inviato al browser via SignalR.

---

## API REST

L'API è documentata via Swagger su `/swagger`. Endpoint principali:

### Impianti e sensori

```
GET  /health                          stato del servizio
GET  /api/devices                     lista dispositivi configurati
GET  /api/sensors/history             storico time-series
     ?from=2024-01-01&to=2024-01-31
     &deviceId=pdc
     &limit=1000
```

### WebSocket / SignalR

```
WS   /hubs/sensors                    hub SignalR real-time
```

Client JavaScript/Blazor:
```js
connection.on("LiveUpdate", (update) => {
  // update.topic, update.payload, update.timestamp
});
```

### Bollette

```
GET  /api/bills                       lista bollette
     ?year=2024&type=Electricity&paid=false
GET  /api/bills/{id}                  dettaglio
GET  /api/bills/{id}/pdf              scarica PDF allegato
POST /api/bills/{id}/paid             segna come pagata
```

### Reminder e manutenzioni

```
GET  /api/reminders                   reminder attivi (entro 14 giorni)
PUT  /api/reminders/{id}/dismiss      dismetti reminder
GET  /api/maintenance                 storico manutenzioni
POST /api/maintenance                 aggiungi record
```

---

## Worker services

| Service | Intervallo | Descrizione |
|---|---|---|
| `ViessmannConnector` | 5 minuti | Legge PDC Vitocal 222-S e VMC Vitovent via Viessmann API (OAuth2) |
| `HuaweiConnector` | 5 minuti | Legge FV e batteria LUNA 2000 via FusionSolar API |
| `HistoryRecorder` | ogni ora | Salva snapshot di tutti i topic MQTT su SQLite |
| `BillWatcher` | 6 ore | Scansiona Gmail, scarica PDF bollette, estrae dati con Claude, salva su DB |

I connector sono **fail-safe**: se le credenziali non sono configurate o l'API esterna non è raggiungibile, logano un warning e saltano il ciclo senza crashare il processo.

---

## Database

SQLite con EF Core. Il file `casatimo.db` viene creato automaticamente al primo avvio nel path configurato da `NAS_DB_PATH`.

### Schema

**SensorReadings** — time-series degli impianti
```
Id | DeviceId | Metric | Value | Unit | MqttTopic | Timestamp
```
Indici su `Timestamp` e `(DeviceId, Metric)`.

**Bills** — bollette
```
Id | Type | Issuer | Amount | DueDate | PeriodFrom | PeriodTo
   | PdfPath | EmailId | CreatedAt | IsPaid | PaidAt | ConsumptionKwh
```
Indici su `DueDate` e `Type`.

**Reminders** — scadenze
```
Id | BillId | DueDate | DaysBefore | IsSent | SentAt | Message
```

**MaintenanceRecords** — manutenzioni impianti
```
Id | DeviceId | Description | Date | Cost | NextDueDate | Notes
```

### Migrations

```bash
cd src/CasaTimo.Api
dotnet ef migrations add InitialCreate --project ../CasaTimo.Infrastructure
dotnet ef database update
```

---

## Docker Compose

```
┌─────────────────────────────────────┐
│  casatimo-mosquitto  :1883 :9001    │  MQTT broker
│  casatimo-api        :5000→:8080    │  REST + SignalR
│  casatimo-workers                   │  background services
└─────────────────────────────────────┘
        volume condiviso: nas_data → /mnt/nas/casatimo
```

Comandi utili:

```bash
docker compose up -d                  # avvia tutto
docker compose up --build -d          # rebuild delle immagini
docker compose logs -f workers        # log dei connector
docker compose logs -f api            # log dell'API
docker compose restart workers        # riavvia i worker (es. dopo cambio .env)
docker compose down                   # ferma tutto (dati persistiti)
docker compose down -v                # ferma tutto e cancella i volumi
```

---

## Accesso remoto

### Cloudflare Tunnel (raccomandato — zero configurazione router)

```bash
# Installa cloudflared sul mini PC
curl -L https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64 \
  -o /usr/local/bin/cloudflared && chmod +x /usr/local/bin/cloudflared

# Autenticazione e creazione tunnel
cloudflared tunnel login
cloudflared tunnel create casatimo
cloudflared tunnel route dns casatimo casa.timo.dev

# Avvia come servizio systemd
cloudflared service install
```

Config `~/.cloudflared/config.yml`:
```yaml
tunnel: <tunnel-id>
credentials-file: /root/.cloudflared/<tunnel-id>.json
ingress:
  - hostname: casa.timo.dev
    service: http://localhost:5000
  - service: http_status:404
```

### CORS

L'API accetta richieste da `http://localhost:5001` e `https://casa.timo.dev`. Per aggiungere altri origins, modifica `Program.cs`:

```csharp
.WithOrigins("http://localhost:5001", "https://casa.timo.dev", "https://altro.dominio.it")
```

---

## Espandibilità

### Aggiungere un nuovo sensore (ESP32 + ESPHome)

1. Configura ESPHome per pubblicare su `casatimo/{device}/{metric}`
2. Zero modifiche al codice: `HistoryRecorder` lo raccoglie automaticamente

### Aggiungere un nuovo connettore

1. Crea `NuovoConnector.cs` in `CasaTimo.Workers` che estende `BackgroundService`
2. Pubblica i dati su MQTT con `IMqttService.PublishAsync`
3. Registra in `Workers/Program.cs`:
   ```csharp
   builder.Services.AddHostedService<NuovoConnector>();
   ```

### Aggiungere una pagina al frontend

1. Crea `src/CasaTimo.Web/Pages/NuovaPagina.razor` con `@page "/nuovo"`
2. Aggiungi il link in `Layout/NavMenu.razor`

### Telecamere (Frigate)

Frigate pubblica eventi su MQTT in `frigate/events`. È sufficiente creare un `FrigateEventService` che si sottoscriva a quel topic e gestisca le notifiche.

---

## Sicurezza

- Tutti i secret in `.env` — mai nel codice o nel repository
- MQTT con autenticazione username/password obbligatoria (`allow_anonymous false`)
- Cloudflare Tunnel: nessuna porta aperta sul router, HTTPS gestito da Cloudflare
- Il NAS DS115j è usato **solo come storage SMB** (ARM 32bit, non supporta Docker)
- Per ambienti di produzione: aggiungere JWT authentication all'API (`builder.Services.AddAuthentication(...)`)

---

## Stack tecnologico

| Layer | Tecnologia |
|---|---|
| Frontend | Blazor WebAssembly (.NET 8) — PWA |
| API | ASP.NET Core Minimal API + SignalR |
| Worker services | C# Background Services |
| Message broker | Eclipse Mosquitto 2 (MQTT) |
| Database | SQLite via EF Core |
| Connettore MQTT | MQTTnet 4.3.7 |
| Parsing bollette | Claude API (Anthropic) |
| Email | Google Gmail API v1 |
| Hosting | Docker Compose su mini PC |
| Accesso remoto | Cloudflare Tunnel |
