"""
CasaTimo — Huawei FusionSolar sidecar
Polling API northbound FusionSolar → MQTT

Topic pubblicati (payload {"value": ..., "unit": "..."}):
  casatimo/fv/power_active     kW   produzione FV istantanea
  casatimo/fv/energy_today     kWh  energia prodotta oggi
  casatimo/fv/battery_soc      %    SOC batteria LUNA 2000
  casatimo/fv/battery_power    kW   potenza batteria (+ carica, - scarica)
  casatimo/fv/grid_power       kW   potenza rete (+ export, - import)
  casatimo/fv/load_power       kW   consumo casa istantaneo
"""
import json, logging, os, time
import paho.mqtt.client as mqtt
import requests

# ── Config ───────────────────────────────────────────────────────────────────
MQTT_HOST  = os.getenv("MQTT_HOST", "localhost")
MQTT_PORT  = int(os.getenv("MQTT_PORT", "1883"))
FS_USER    = os.getenv("FUSIONSOLAR_USER", "")
FS_SYSCODE = os.getenv("FUSIONSOLAR_SYSCODE", "")
POLL_SECS  = int(os.getenv("POLL_INTERVAL_SECONDS", "300"))
BASE_URL   = "https://eu5.fusionsolar.huawei.com"

DEV_INVERTER = 1
DEV_BATTERY  = 39
DEV_GRID     = 47

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger(__name__)

session = requests.Session()
session.headers.update({"Content-Type": "application/json"})

# ── Auth ─────────────────────────────────────────────────────────────────────
def login():
    resp = session.post(f"{BASE_URL}/thirdData/login",
                        json={"userName": FS_USER, "systemCode": FS_SYSCODE},
                        timeout=15)
    resp.raise_for_status()
    data = resp.json()
    if not data.get("success"):
        raise RuntimeError(f"Login fallito: code={data.get('failCode')} msg={data.get('message')}")
    log.info("FusionSolar login OK")


def api_post(path: str, body: dict) -> dict:
    # Piccola pausa tra chiamate per rispettare i rate limit Huawei (1 req/s)
    time.sleep(1)
    resp = session.post(f"{BASE_URL}{path}", json=body, timeout=15)
    resp.raise_for_status()
    data = resp.json()
    # failCode 401 = sessione scaduta
    if data.get("failCode") == 401:
        log.warning("Sessione scaduta, ri-login...")
        login()
        time.sleep(1)
        resp = session.post(f"{BASE_URL}{path}", json=body, timeout=15)
        resp.raise_for_status()
        data = resp.json()
    if not data.get("success"):
        raise RuntimeError(f"API {path}: code={data.get('failCode')} msg={data.get('message')}")
    return data

# ── Discovery ────────────────────────────────────────────────────────────────
def get_station_code() -> str:
    data = api_post("/thirdData/getStationList", {})
    stations = data.get("data", [])
    if not stations:
        raise RuntimeError("Nessuna installazione trovata in FusionSolar")
    code = stations[0]["stationCode"]
    log.info(f"Stazione: {stations[0].get('stationName', '?')} ({code})")
    return code


def get_devices(station_code: str) -> dict[int, list[int]]:
    """Ritorna {dev_type_id: [device_id, ...]} per inverter, batteria e meter."""
    data = api_post("/thirdData/getDevList", {"stationCodes": station_code})
    by_type: dict[int, list[int]] = {}
    for dev in data.get("data", []):
        t = dev.get("devTypeId")
        if t in (DEV_INVERTER, DEV_BATTERY, DEV_GRID):
            by_type.setdefault(t, []).append(int(dev["id"]))
            log.info(f"  Device tipo {t}: {dev.get('devName', '?')} (id={dev['id']})")
    return by_type

# ── Polling ──────────────────────────────────────────────────────────────────
def poll(station_code: str, devices: dict[int, list[int]]) -> dict[str, tuple[float, str]]:
    readings: dict[str, tuple[float, str]] = {}

    # Energia giornaliera dalla stazione
    kpi = api_post("/thirdData/getStationRealKpi", {"stationCodes": station_code})
    for row in kpi.get("data", []):
        if row.get("stationCode") == station_code:
            d = row.get("dataItemMap", {})
            if d.get("day_power") is not None:
                readings["energy_today"] = (float(d["day_power"] or 0), "kWh")
            break

    # Inverter → potenza attiva + consumo casa
    if DEV_INVERTER in devices:
        inv = api_post("/thirdData/getDevRealKpi", {
            "devIds": ",".join(str(i) for i in devices[DEV_INVERTER]),
            "devTypeId": DEV_INVERTER,
        })
        for row in inv.get("data", []):
            d = row.get("dataItemMap", {})
            if d.get("active_power") is not None:
                readings["power_active"] = (float(d["active_power"] or 0), "kW")
            if d.get("load_power") is not None:
                readings["load_power"] = (float(d["load_power"] or 0), "kW")

    # Batteria LUNA 2000 → SOC e potenza
    if DEV_BATTERY in devices:
        bat = api_post("/thirdData/getDevRealKpi", {
            "devIds": ",".join(str(i) for i in devices[DEV_BATTERY]),
            "devTypeId": DEV_BATTERY,
        })
        for row in bat.get("data", []):
            d = row.get("dataItemMap", {})
            if d.get("battery_soc") is not None:
                readings["battery_soc"] = (float(d["battery_soc"] or 0), "%")
            if d.get("ch_discharge_power") is not None:
                readings["battery_power"] = (float(d["ch_discharge_power"] or 0), "kW")

    # Grid meter → import/export
    if DEV_GRID in devices:
        grid = api_post("/thirdData/getDevRealKpi", {
            "devIds": ",".join(str(i) for i in devices[DEV_GRID]),
            "devTypeId": DEV_GRID,
        })
        for row in grid.get("data", []):
            d = row.get("dataItemMap", {})
            if d.get("active_power") is not None:
                readings["grid_power"] = (float(d["active_power"] or 0), "kW")

    return readings

# ── MQTT ─────────────────────────────────────────────────────────────────────
def connect_mqtt() -> mqtt.Client:
    client = mqtt.Client(client_id="casatimo-huawei")
    client.connect(MQTT_HOST, MQTT_PORT, keepalive=60)
    client.loop_start()
    log.info(f"Connesso a MQTT {MQTT_HOST}:{MQTT_PORT}")
    return client


def publish(client: mqtt.Client, readings: dict[str, tuple[float, str]]):
    for suffix, (value, unit) in readings.items():
        topic = f"casatimo/fv/{suffix}"
        client.publish(topic, json.dumps({"value": value, "unit": unit}))
        log.info(f"  {topic:<45} = {value} {unit}")

# ── Main ─────────────────────────────────────────────────────────────────────
def main():
    if not FS_USER or not FS_SYSCODE:
        raise RuntimeError("FUSIONSOLAR_USER e FUSIONSOLAR_SYSCODE sono obbligatori")

    mqtt_client = connect_mqtt()
    login()

    station_code = get_station_code()
    devices      = get_devices(station_code)

    log.info(f"Polling ogni {POLL_SECS}s — inizio...")
    while True:
        try:
            log.info("--- Poll ---")
            readings = poll(station_code, devices)
            if readings:
                publish(mqtt_client, readings)
            else:
                log.warning("Nessun dato ricevuto dall'API")
        except Exception as e:
            log.error(f"Errore poll: {e}")
        time.sleep(POLL_SECS)


if __name__ == "__main__":
    main()
