# Casa Timò — Guida al Deploy (v0.0)

Questa guida descrive come installare e avviare l'applicazione su un mini PC locale (es. Beelink N305) con accesso remoto via Cloudflare Tunnel.

---

## Requisiti del server

| Requisito | Versione minima |
|---|---|
| Sistema operativo | Ubuntu 22.04 LTS o Debian 12 |
| Docker Engine | 24+ |
| Docker Compose | v2.20+ (incluso in Docker Engine) |
| RAM | 2 GB |
| Disco | 10 GB liberi |

---

## 1. Installazione Docker (se non presente)

```bash
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER
# Esci e rientra per applicare il gruppo
```

---

## 2. Clona il repository

```bash
git clone https://github.com/DavideTimo/homeBrain.git
cd homeBrain
```

---

## 3. Configura i segreti

```bash
cp .env.example .env
nano .env
```

Compila **tutti** i campi obbligatori nel file `.env`:

```env
Jwt__Key=una_chiave_segreta_di_almeno_32_caratteri!!
Auth__Username=davide
Auth__Password=la_tua_password_sicura
```

> **Importante:** `.env` non viene mai committato su Git (è in `.gitignore`).
> Se perdi il file, ricrealo da `.env.example`.

---

## 4. Configura l'accesso alle immagini Docker (GitHub Container Registry)

Le immagini vengono pubblicate automaticamente su `ghcr.io` ad ogni push su `main`.
Per scaricarle dal server devi autenticarti una volta sola:

```bash
# Crea un Personal Access Token su GitHub con scope "read:packages"
# Settings → Developer settings → Personal access tokens → Tokens (classic)
echo "IL_TUO_TOKEN" | docker login ghcr.io -u DavideTimo --password-stdin
```

---

## 5. Avvia l'applicazione

```bash
docker compose pull        # scarica le ultime immagini
docker compose up -d       # avvia tutti i servizi in background
docker compose logs -f     # (opzionale) segui i log in tempo reale
```

### Servizi avviati

| Servizio | Porta locale | Descrizione |
|---|---|---|
| `casatimo-web` | 5001 | Blazor WASM (frontend — MudBlazor) |
| `casatimo-api` | 5233 | ASP.NET Core API |
| `casatimo-workers` | — | Background services |
| `casatimo-mosquitto` | 1883, 9001 | MQTT broker |
| `casatimo-watchtower` | — | Auto-aggiornamento container |

Apri il browser su `http://IP_DEL_SERVER:5001` per verificare che funzioni.

---

## 6. Accesso remoto con Cloudflare Tunnel

Cloudflare Tunnel permette di accedere all'app da internet senza aprire porte sul router.

### Installazione cloudflared

```bash
curl -L https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64 \
  -o /usr/local/bin/cloudflared
chmod +x /usr/local/bin/cloudflared
```

### Login e creazione tunnel

```bash
cloudflared tunnel login
cloudflared tunnel create casatimo
```

### Configura il tunnel

Crea `/etc/cloudflared/config.yml`:

```yaml
tunnel: casatimo
credentials-file: /root/.cloudflared/<ID_TUNNEL>.json

ingress:
  - hostname: casa.tuodominio.com
    service: http://localhost:5001
  - service: http_status:404
```

### Avvia cloudflared come servizio

```bash
cloudflared service install
systemctl enable cloudflared
systemctl start cloudflared
```

### DNS su Cloudflare

Nel pannello Cloudflare del tuo dominio, aggiungi un record CNAME:
- **Nome:** `casa`
- **Target:** `<ID_TUNNEL>.cfargotunnel.com`
- **Proxy:** attivo (arancione)

L'app sarà raggiungibile su `https://casa.tuodominio.com`.

---

## 7. Auto-aggiornamento

Il sistema si aggiorna automaticamente in due modi:

### Container Docker (Watchtower)
`casatimo-watchtower` controlla ogni 5 minuti se ci sono nuove immagini su `ghcr.io`.
Quando trovi una nuova versione (pubblicata da GitHub Actions ad ogni push su `main`), la scarica e riavvia i container interessati senza downtime significativo.

Non devi fare nulla: ogni volta che committa su `main`, il server si aggiorna da solo entro ~5 minuti.

### App Blazor (PWA Service Worker)
Quando apri l'app dopo un aggiornamento, il browser scarica il nuovo service worker in background.
Appena pronto, compare un banner in cima alla pagina:

> 🔄 È disponibile una nuova versione di Casa Timò. **[Aggiorna ora]**

Clicca il pulsante per applicare l'aggiornamento (ricarica la pagina con la nuova versione).

---

## 8. Aggiornamento manuale (alternativa)

Se preferisci controllare tu quando aggiornare:

```bash
cd homeBrain
git pull                   # scarica le ultime modifiche al docker-compose
docker compose pull        # scarica le nuove immagini
docker compose up -d       # riavvia con le nuove immagini
```

---

## 9. Backup

I dati persistenti sono in volumi Docker:

```bash
# Backup del database SQLite
docker run --rm \
  -v casatimo_data:/data \
  -v $(pwd)/backup:/backup \
  alpine tar czf /backup/casatimo-db-$(date +%Y%m%d).tar.gz /data

# Ripristino
docker run --rm \
  -v casatimo_data:/data \
  -v $(pwd)/backup:/backup \
  alpine tar xzf /backup/casatimo-db-YYYYMMDD.tar.gz -C /
```

Per i PDF delle bollette, monta il NAS su `/mnt/nas` e configura i backup del NAS stesso.

---

## 10. Comandi utili

```bash
# Stato dei container
docker compose ps

# Log di un servizio specifico
docker compose logs -f api
docker compose logs -f workers

# Riavvio di un singolo servizio
docker compose restart api

# Ferma tutto
docker compose down

# Ferma tutto e rimuovi i volumi (ATTENZIONE: cancella i dati)
docker compose down -v
```

---

## 11. Credenziali Viessmann (opzionale)

Se hai la pompa di calore Viessmann, registra le credenziali API su [developer.viessmann.com](https://developer.viessmann.com) e aggiungile al `.env`:

```env
Viessmann__ApiBaseUrl=https://api.viessmann.com
Viessmann__TokenEndpoint=https://iam.viessmann.com/idp/v3/token
Viessmann__ClientId=il_tuo_client_id
Viessmann__ClientSecret=il_tuo_client_secret
```

Il connettore inizierà a pubblicare i dati su MQTT e a storicizzarli nel database automaticamente.

---

*Documento aggiornato: giugno 2026 — versione app 0.0*
