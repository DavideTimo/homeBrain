"""
CasaTimo — Huawei sidecar
Due modalità (HUAWEI_MODE):
  - modbus (default): lettura locale via Modbus TCP direttamente dall'inverter
    (SUN2000 in LAN, porta 6607). Nessuna credenziale cloud richiesta.
  - cloud: polling API northbound FusionSolar (richiede account "installer",
    permessi da richiedere separatamente). Tenuta come opzione futura.

Topic pubblicati in entrambe le modalità (payload {"value": ..., "unit": "..."}):
  casatimo/fv/power_active     kW   produzione FV istantanea
  casatimo/fv/energy_today     kWh  energia prodotta oggi
  casatimo/fv/battery_soc      %    SOC batteria LUNA 2000 (solo se presente)
  casatimo/fv/battery_power    kW   potenza batteria (+ carica, - scarica) (solo se presente)
  casatimo/fv/grid_power       kW   potenza rete (+ export, - import) (solo se power meter presente)
  casatimo/fv/load_power       kW   consumo casa istantaneo (solo se power meter presente)
"""
import asyncio, json, logging, os, time
import paho.mqtt.client as mqtt

# ── Config comune ────────────────────────────────────────────────────────────
MQTT_HOST  = os.getenv("MQTT_HOST", "localhost")
MQTT_PORT  = int(os.getenv("MQTT_PORT", "1883"))
POLL_SECS  = int(os.getenv("POLL_INTERVAL_SECONDS", "300"))
HUAWEI_MODE = os.getenv("HUAWEI_MODE", "modbus").lower()

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger(__name__)

# ── MQTT (condiviso tra le due modalità) ─────────────────────────────────────
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


# ── Modalità MODBUS TCP locale (default) ─────────────────────────────────────
# Legge direttamente l'inverter sulla LAN via python-huawei-solar, senza cloud.
# Prerequisiti: IP locale dell'inverter + Modbus TCP abilitato dall'app
# FusionSolar (Dispositivi → Impostazioni → Configurazione comunicazione).
MODBUS_HOST     = os.getenv("HUAWEI_INVERTER_HOST", "")
MODBUS_PORT     = int(os.getenv("HUAWEI_INVERTER_PORT", "6607"))
MODBUS_SLAVE_ID = int(os.getenv("HUAWEI_INVERTER_SLAVE_ID", "0"))


def _normalize(result) -> tuple[float, str]:
    """Converte i valori Modbus (tipicamente W / Wh) nelle unità kW / kWh usate
    dagli altri topic MQTT di CasaTimo. Lascia invariate le altre unità (es. %)."""
    value, unit = result.value, result.unit
    if unit == "W":
        return value / 1000, "kW"
    if unit == "Wh":
        return value / 1000, "kWh"
    return value, unit


async def poll_modbus(bridge, rn) -> dict[str, tuple[float, str]]:
    registers = [rn.ACTIVE_POWER, rn.DAILY_YIELD_ENERGY]

    from huawei_solar import register_values as rv
    has_battery = bridge.battery_type != rv.StorageProductModel.NONE
    has_meter = bridge.power_meter_online

    if has_battery:
        registers += [rn.STORAGE_STATE_OF_CAPACITY, rn.STORAGE_CHARGE_DISCHARGE_POWER]
    if has_meter:
        registers += [rn.POWER_METER_ACTIVE_POWER, rn.LOAD_POWER]

    results = await bridge.batch_update(registers)

    readings: dict[str, tuple[float, str]] = {}
    readings["power_active"] = _normalize(results[rn.ACTIVE_POWER])
    readings["energy_today"] = _normalize(results[rn.DAILY_YIELD_ENERGY])
    if has_battery:
        readings["battery_soc"] = _normalize(results[rn.STORAGE_STATE_OF_CAPACITY])
        readings["battery_power"] = _normalize(results[rn.STORAGE_CHARGE_DISCHARGE_POWER])
    if has_meter:
        readings["grid_power"] = _normalize(results[rn.POWER_METER_ACTIVE_POWER])
        readings["load_power"] = _normalize(results[rn.LOAD_POWER])

    return readings


async def run_modbus():
    from huawei_solar import create_tcp_bridge, register_names as rn

    if not MODBUS_HOST:
        raise RuntimeError(
            "HUAWEI_INVERTER_HOST è obbligatorio in modalità modbus "
            "(IP locale dell'inverter sulla LAN)"
        )

    mqtt_client = connect_mqtt()

    log.info(f"Connessione Modbus TCP a {MODBUS_HOST}:{MODBUS_PORT} (slave_id={MODBUS_SLAVE_ID})...")
    bridge = await create_tcp_bridge(host=MODBUS_HOST, port=MODBUS_PORT, slave_id=MODBUS_SLAVE_ID)
    log.info(f"Connesso — batteria: {bridge.battery_type.name} | power meter online: {bridge.power_meter_online}")

    try:
        log.info(f"Polling ogni {POLL_SECS}s — inizio...")
        while True:
            try:
                log.info("--- Poll (modbus) ---")
                readings = await poll_modbus(bridge, rn)
                if readings:
                    publish(mqtt_client, readings)
                else:
                    log.warning("Nessun dato letto dall'inverter")
            except Exception as e:
                log.error(f"Errore poll modbus: {e}")
            await asyncio.sleep(POLL_SECS)
    finally:
        await bridge.stop()


# ── Modalità CLOUD FusionSolar (opzionale, richiede account installer) ──────
# Mantenuta come fallback/opzione futura: usa la API northbound FusionSolar,
# che richiede permessi "installer" da richiedere a chi ha eseguito l'impianto.
FS_USER    = os.getenv("FUSIONSOLAR_USER", "")
FS_SYSCODE = os.getenv("FUSIONSOLAR_SYSCODE", "")
FS_BASE_URL = "https://eu5.fusionsolar.huawei.com"

DEV_INVERTER = 1
DEV_BATTERY  = 39
DEV_GRID     = 47


def _cloud_session():
    import requests
    session = requests.Session()
    session.headers.update({"Content-Type": "application/json"})
    return session


def _cloud_login(session):
    resp = session.post(f"{FS_BASE_URL}/thirdData/login",
                         json={"userName": FS_USER, "systemCode": FS_SYSCODE},
                         timeout=15)
    resp.raise_for_status()
    data = resp.json()
    if not data.get("success"):
        raise RuntimeError(f"Login fallito: code={data.get('failCode')} msg={data.get('message')}")
    log.info("FusionSolar login OK")


def _cloud_api_post(session, path: str, body: dict) -> dict:
    # Piccola pausa tra chiamate per rispettare i rate limit Huawei (1 req/s)
    time.sleep(1)
    resp = session.post(f"{FS_BASE_URL}{path}", json=body, timeout=15)
    resp.raise_for_status()
    data = resp.json()
    # failCode 401 = sessione scaduta
    if data.get("failCode") == 401:
        log.warning("Sessione scaduta, ri-login...")
        _cloud_login(session)
        time.sleep(1)
        resp = session.post(f"{FS_BASE_URL}{path}", json=body, timeout=15)
        resp.raise_for_status()
        data = resp.json()
    if not data.get("success"):
        raise RuntimeError(f"API {path}: code={data.get('failCode')} msg={data.get('message')}")
    return data


def _cloud_get_station_code(session) -> str:
    data = _cloud_api_post(session, "/thirdData/getStationList", {})
    stations = data.get("data", [])
    if not stations:
        raise RuntimeError("Nessuna installazione trovata in FusionSolar")
    code = stations[0]["stationCode"]
    log.info(f"Stazione: {stations[0].get('stationName', '?')} ({code})")
    return code


def _cloud_get_devices(session, station_code: str) -> dict[int, list[int]]:
    """Ritorna {dev_type_id: [device_id, ...]} per inverter, batteria e meter."""
    data = _cloud_api_post(session, "/thirdData/getDevList", {"stationCodes": station_code})
    by_type: dict[int, list[int]] = {}
    for dev in data.get("data", []):
        t = dev.get("devTypeId")
        if t in (DEV_INVERTER, DEV_BATTERY, DEV_GRID):
            by_type.setdefault(t, []).append(int(dev["id"]))
            log.info(f"  Device tipo {t}: {dev.get('devName', '?')} (id={dev['id']})")
    return by_type


def _cloud_poll(session, station_code: str, devices: dict[int, list[int]]) -> dict[str, tuple[float, str]]:
    readings: dict[str, tuple[float, str]] = {}

    # Energia giornaliera dalla stazione
    kpi = _cloud_api_post(session, "/thirdData/getStationRealKpi", {"stationCodes": station_code})
    for row in kpi.get("data", []):
        if row.get("stationCode") == station_code:
            d = row.get("dataItemMap", {})
            if d.get("day_power") is not None:
                readings["energy_today"] = (float(d["day_power"] or 0), "kWh")
            break

    # Inverter → potenza attiva + consumo casa
    if DEV_INVERTER in devices:
        inv = _cloud_api_post(session, "/thirdData/getDevRealKpi", {
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
        bat = _cloud_api_post(session, "/thirdData/getDevRealKpi", {
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
        grid = _cloud_api_post(session, "/thirdData/getDevRealKpi", {
            "devIds": ",".join(str(i) for i in devices[DEV_GRID]),
            "devTypeId": DEV_GRID,
        })
        for row in grid.get("data", []):
            d = row.get("dataItemMap", {})
            if d.get("active_power") is not None:
                readings["grid_power"] = (float(d["active_power"] or 0), "kW")

    return readings


def run_cloud():
    if not FS_USER or not FS_SYSCODE:
        raise RuntimeError("FUSIONSOLAR_USER e FUSIONSOLAR_SYSCODE sono obbligatori in modalità cloud")

    mqtt_client = connect_mqtt()
    session = _cloud_session()
    _cloud_login(session)

    station_code = _cloud_get_station_code(session)
    devices      = _cloud_get_devices(session, station_code)

    log.info(f"Polling ogni {POLL_SECS}s — inizio...")
    while True:
        try:
            log.info("--- Poll (cloud) ---")
            readings = _cloud_poll(session, station_code, devices)
            if readings:
                publish(mqtt_client, readings)
            else:
                log.warning("Nessun dato ricevuto dall'API")
        except Exception as e:
            log.error(f"Errore poll cloud: {e}")
        time.sleep(POLL_SECS)


# ── Main ─────────────────────────────────────────────────────────────────────
if __name__ == "__main__":
    log.info(f"CasaTimo Huawei sidecar — modalità: {HUAWEI_MODE}")
    if HUAWEI_MODE == "cloud":
        run_cloud()
    elif HUAWEI_MODE == "modbus":
        asyncio.run(run_modbus())
    else:
        raise RuntimeError(f"HUAWEI_MODE sconosciuto: '{HUAWEI_MODE}' (valori validi: modbus, cloud)")
