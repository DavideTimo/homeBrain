"""
CasaTimo — Viessmann sidecar
Legge i dati dalla Vitocal 222-S tramite PyViCare e pubblica su MQTT.

Topic pubblicati (payload JSON: {"value": <float>, "unit": "<str>"}):
  casatimo/pdc/outdoor_temperature        °C  temperatura esterna
  casatimo/pdc/supply_temperature         °C  temperatura mandata
  casatimo/pdc/return_temperature         °C  temperatura ritorno
  casatimo/pdc/dhw_temperature            °C  acqua calda sanitaria
  casatimo/pdc/circuit0_supply_temp       °C  mandata circuito 0
  casatimo/pdc/circuit0_room_temp         °C  temperatura ambiente circuito 0
  casatimo/pdc/compressor_active          —   1 = compressore on, 0 = off
  casatimo/pdc/mode                       —   stringa modalità operativa
"""

import json
import logging
import os
import time

import paho.mqtt.client as mqtt
from PyViCare.PyViCare import PyViCare

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    datefmt="%Y-%m-%dT%H:%M:%S",
)
log = logging.getLogger(__name__)

MQTT_HOST = os.getenv("MQTT_HOST", "mosquitto")
MQTT_PORT = int(os.getenv("MQTT_PORT", "1883"))
MQTT_USER = os.getenv("MQTT_USER", "")
MQTT_PASS = os.getenv("MQTT_PASS", "")
VIESSMANN_USER = os.getenv("VIESSMANN_USER", "")
VIESSMANN_PASS = os.getenv("VIESSMANN_PASS", "")
CLIENT_ID = os.getenv("VIESSMANN_CLIENT_ID", "viessmann.vicare.android")
TOKEN_FILE = os.getenv("TOKEN_FILE", "/data/viessmann_token.json")
POLL_SECS = int(os.getenv("POLL_INTERVAL_SECONDS", "300"))


def safe_read(fn):
    """Chiama fn() ignorando eccezioni; ritorna None se non supportato."""
    try:
        result = fn()
        return result
    except Exception as e:
        log.debug("Metrica non disponibile: %s", e)
        return None


def publish(client: mqtt.Client, metric: str, value, unit: str | None = None):
    if value is None:
        return
    try:
        numeric = round(float(value), 2)
        payload = json.dumps({"value": numeric, "unit": unit})
    except (TypeError, ValueError):
        payload = json.dumps({"value": value, "unit": unit})
    topic = f"casatimo/pdc/{metric}"
    client.publish(topic, payload, retain=True)
    log.info("  %-40s = %s %s", topic, value, unit or "")


def poll_device(device, mqtt_client: mqtt.Client):
    hp = device.asHeatPump()

    # Temperature generali
    publish(mqtt_client, "outdoor_temperature",     safe_read(hp.getOutsideTemperature),               "°C")
    publish(mqtt_client, "supply_temperature",      safe_read(hp.getSupplyTemperature),                "°C")
    publish(mqtt_client, "dhw_temperature",         safe_read(hp.getDomesticHotWaterStorageTemperature),"°C")

    # Compressore
    compressor_on = safe_read(hp.getCompressorActive)
    if compressor_on is not None:
        publish(mqtt_client, "compressor_active", 1 if compressor_on else 0)

    # Circuiti riscaldamento
    try:
        for i, circuit in enumerate(hp.circuits):
            publish(mqtt_client, f"circuit{i}_supply_temp",
                    safe_read(circuit.getSupplyTemperature), "°C")
            publish(mqtt_client, f"circuit{i}_return_temp",
                    safe_read(circuit.getReturnTemperature), "°C")
            publish(mqtt_client, f"circuit{i}_room_temp",
                    safe_read(circuit.getRoomTemperature), "°C")
            publish(mqtt_client, f"circuit{i}_mode",
                    safe_read(circuit.getActiveMode))
    except Exception as e:
        log.warning("Errore lettura circuiti: %s", e)

    # ACS (acqua calda sanitaria)
    try:
        for i, dhw in enumerate(hp.dhw):
            publish(mqtt_client, f"dhw{i}_temperature",
                    safe_read(dhw.getTemperature), "°C")
            publish(mqtt_client, f"dhw{i}_storage_temperature",
                    safe_read(dhw.getStorageTemperature), "°C")
    except Exception as e:
        log.warning("Errore lettura DHW: %s", e)


def connect_mqtt() -> mqtt.Client:
    client = mqtt.Client(client_id="casatimo-viessmann-sidecar")
    if MQTT_USER:
        client.username_pw_set(MQTT_USER, MQTT_PASS)
    client.connect(MQTT_HOST, MQTT_PORT)
    client.loop_start()
    log.info("Connesso a MQTT %s:%s", MQTT_HOST, MQTT_PORT)
    return client


def connect_viessmann() -> PyViCare:
    if not os.path.exists(TOKEN_FILE):
        raise FileNotFoundError(
            f"Token non trovato: {TOKEN_FILE}\n"
            "Esegui prima setup_token.py per autenticarti con Viessmann."
        )
    vicare = PyViCare()
    vicare.initWithCredentials(VIESSMANN_USER, VIESSMANN_PASS, CLIENT_ID, TOKEN_FILE)
    log.info("Connesso a Viessmann — %d dispositivo/i trovato/i", len(vicare.devices))
    for d in vicare.devices:
        log.info("  Dispositivo: %s", d.getModel())
    return vicare


def main():
    mqtt_client = connect_mqtt()
    vicare = connect_viessmann()
    device = vicare.devices[0]

    log.info("Polling ogni %ds — avvio...", POLL_SECS)
    while True:
        try:
            log.info("--- Poll Viessmann ---")
            poll_device(device, mqtt_client)
        except Exception as e:
            log.error("Errore durante il polling: %s", e)
        time.sleep(POLL_SECS)


if __name__ == "__main__":
    main()
