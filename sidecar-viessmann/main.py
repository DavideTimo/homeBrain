"""
CasaTimo — Viessmann sidecar v2
Polling diretto API REST Viessmann → MQTT.
Nessuna dipendenza da PyViCare: solo requests + paho-mqtt.

Topic pubblicati (payload {"value": ..., "unit": "..."}):
  casatimo/pdc/outdoor_temperature
  casatimo/pdc/return_temperature
  casatimo/pdc/supply_temperature
  casatimo/pdc/dhw_temperature
  casatimo/pdc/mode
  casatimo/pdc/compressor_active
"""
import base64, hashlib, json, logging, os, secrets, time
from pathlib import Path
from urllib.parse import parse_qs, urlparse

import paho.mqtt.client as mqtt
import requests

# ── Config da variabili d'ambiente ─────────────────────────────────────────
MQTT_HOST  = os.getenv("MQTT_HOST", "mosquitto")
MQTT_PORT  = int(os.getenv("MQTT_PORT", "1883"))
MQTT_USER  = os.getenv("MQTT_USER", "")
MQTT_PASS  = os.getenv("MQTT_PASS", "")
V_USER     = os.getenv("VIESSMANN_USER", "")
V_PASS     = os.getenv("VIESSMANN_PASS", "")
CLIENT_ID  = os.getenv("VIESSMANN_CLIENT_ID", "")
TOKEN_FILE = Path(os.getenv("TOKEN_FILE", "/data/viessmann_token.json"))
POLL_SECS  = int(os.getenv("POLL_INTERVAL_SECONDS", "300"))

AUTHORIZE_URL = "https://iam.viessmann-climatesolutions.com/idp/v3/authorize"
TOKEN_URL     = "https://iam.viessmann-climatesolutions.com/idp/v3/token"
API_BASE      = "https://api.viessmann-climatesolutions.com/iot/v2"
REDIRECT_URI  = "vicare://oauth-callback/everest"

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger(__name__)

# ── PKCE helpers ────────────────────────────────────────────────────────────
def _pkce_pair():
    v = base64.urlsafe_b64encode(secrets.token_bytes(48)).rstrip(b"=").decode()
    c = base64.urlsafe_b64encode(hashlib.sha256(v.encode()).digest()).rstrip(b"=").decode()
    return v, c

# ── Auth ────────────────────────────────────────────────────────────────────
def _authenticate() -> dict:
    verifier, challenge = _pkce_pair()
    params = {
        "response_type": "code", "client_id": CLIENT_ID,
        "redirect_uri": REDIRECT_URI, "scope": "IoT offline_access",
        "code_challenge": challenge, "code_challenge_method": "S256",
    }
    resp = requests.post(
        AUTHORIZE_URL, params=params,
        headers={"Content-Type": "application/x-www-form-urlencoded"},
        auth=(V_USER, V_PASS), allow_redirects=False, timeout=15,
    )
    if resp.status_code != 302 or "Location" not in resp.headers:
        raise RuntimeError(f"Auth fallita HTTP {resp.status_code}: {resp.text[:200]}")

    parsed = urlparse(resp.headers["Location"])
    code = (parse_qs(parsed.query).get("code") or
            parse_qs(parsed.fragment).get("code") or [None])[0]
    if not code:
        raise RuntimeError(f"Code non trovato nel redirect: {resp.headers['Location'][:200]}")

    tok = requests.post(TOKEN_URL, data={
        "grant_type": "authorization_code", "code": code,
        "client_id": CLIENT_ID, "redirect_uri": REDIRECT_URI,
        "code_verifier": verifier,
    }, timeout=15)
    tok.raise_for_status()
    token = tok.json()
    token["obtained_at"] = time.time()
    return token


def _refresh(token: dict) -> dict:
    resp = requests.post(TOKEN_URL, data={
        "grant_type": "refresh_token",
        "refresh_token": token["refresh_token"],
        "client_id": CLIENT_ID,
    }, timeout=15)
    resp.raise_for_status()
    new = resp.json()
    new["obtained_at"] = time.time()
    return new


def _load() -> dict | None:
    if TOKEN_FILE.exists():
        try:
            return json.loads(TOKEN_FILE.read_text())
        except Exception:
            pass
    return None


def _save(token: dict):
    TOKEN_FILE.parent.mkdir(parents=True, exist_ok=True)
    TOKEN_FILE.write_text(json.dumps(token, indent=2))


def get_token() -> dict:
    token = _load()
    if token:
        age  = time.time() - token.get("obtained_at", 0)
        exp  = token.get("expires_in", 3600)
        if age < exp - 120:
            return token
        if "refresh_token" in token:
            log.info("Token scaduto — rinnovo con refresh_token...")
            try:
                token = _refresh(token)
                _save(token)
                log.info("Token rinnovato (scade tra %ds)", token.get("expires_in", 0))
                return token
            except Exception as e:
                log.warning("Refresh fallito (%s) — riautentico", e)

    log.info("Nuova autenticazione PKCE...")
    token = _authenticate()
    _save(token)
    log.info("Auth OK — scope=%s expires_in=%ds refresh=%s",
             token.get("scope"), token.get("expires_in", 0),
             "✅" if "refresh_token" in token else "❌")
    return token

# ── API helpers ─────────────────────────────────────────────────────────────
def _get(token: dict, path: str) -> dict:
    r = requests.get(f"{API_BASE}{path}",
                     headers={"Authorization": f"Bearer {token['access_token']}"},
                     timeout=15)
    r.raise_for_status()
    return r.json()


def _prop(data: dict, key: str):
    try:
        return data["data"]["properties"][key]["value"]
    except (KeyError, TypeError):
        return None

# ── Discover ────────────────────────────────────────────────────────────────
def discover(token: dict) -> tuple[int, str, str]:
    inst = _get(token, "/equipment/installations?includeGateways=true")["data"][0]
    inst_id  = inst["id"]
    gateway  = inst["gateways"][0]["serial"]
    devices  = _get(token, f"/equipment/installations/{inst_id}/gateways/{gateway}/devices")["data"]
    device   = next((d["id"] for d in devices if d.get("deviceType") == "heating"), "0")
    log.info("Installazione %d | Gateway %s | Device %s", inst_id, gateway, device)
    return inst_id, gateway, device

# ── Poll & publish ───────────────────────────────────────────────────────────
FEATURES = [
    # (feature_path,                                     mqtt_topic,         unit,  property_key)
    ("heating.sensors.temperature.outside",              "pdc/outdoor_temp", "°C",  "value"),
    ("heating.sensors.temperature.return",               "pdc/return_temp",  "°C",  "value"),
    ("heating.circuits.0.sensors.temperature.supply",    "pdc/supply_temp",  "°C",  "value"),
    ("heating.dhw.sensors.temperature.hotWaterStorage",  "pdc/dhw_temp",     "°C",  "value"),
]

# Modalità operative → codice numerico (salvabile in SensorReading.Value double)
MODE_CODES = {
    "standby": 0, "heating": 1, "cooling": 2,
    "dhwAndHeating": 3, "dhwAndCooling": 4, "dhw": 5,
    "forcedNormalMode": 6, "off": 7,
}


def poll(token: dict, mc: mqtt.Client, inst_id: int, gw: str, dev: str):
    base = f"/features/installations/{inst_id}/gateways/{gw}/devices/{dev}/features"

    for feat, topic, unit, prop in FEATURES:
        try:
            val = _prop(_get(token, f"{base}/{feat}"), prop)
            if val is None:
                continue
            mc.publish(f"casatimo/{topic}", json.dumps({"value": val, "unit": unit}), retain=True)
            log.info("  casatimo/%-38s = %s %s", topic, val, unit or "")
        except Exception as e:
            log.debug("  %s → %s", feat, e)

    # Compressore
    try:
        props = _get(token, f"{base}/heating.compressors.0")["data"]["properties"]
        active = props["active"]["value"]
        mc.publish("casatimo/pdc/compressor_active",
                   json.dumps({"value": 1 if active else 0, "unit": None}), retain=True)
        log.info("  casatimo/pdc/compressor_active = %s", active)
    except Exception as e:
        log.debug("  compressor → %s", e)

    # Modalità operativa → codice numerico
    try:
        mode_str = _prop(_get(token, f"{base}/heating.circuits.0.operating.modes.active"), "value")
        if mode_str is not None:
            code = MODE_CODES.get(mode_str, -1)
            mc.publish("casatimo/pdc/mode_code",
                       json.dumps({"value": code, "unit": mode_str}), retain=True)
            log.info("  casatimo/pdc/mode_code = %d (%s)", code, mode_str)
    except Exception as e:
        log.debug("  mode → %s", e)


# ── Main ────────────────────────────────────────────────────────────────────
def main():
    mc = mqtt.Client(client_id="casatimo-viessmann-sidecar")
    if MQTT_USER:
        mc.username_pw_set(MQTT_USER, MQTT_PASS)
    mc.connect(MQTT_HOST, MQTT_PORT)
    mc.loop_start()
    log.info("Connesso a MQTT %s:%s", MQTT_HOST, MQTT_PORT)

    token            = get_token()
    inst_id, gw, dev = discover(token)

    log.info("Polling ogni %ds — inizio...", POLL_SECS)
    while True:
        try:
            log.info("--- Poll ---")
            token = get_token()
            poll(token, mc, inst_id, gw, dev)
        except Exception as e:
            log.error("Errore: %s", e)
        time.sleep(POLL_SECS)


if __name__ == "__main__":
    main()
