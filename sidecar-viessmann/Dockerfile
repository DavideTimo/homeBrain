FROM python:3.12-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py setup_token.py ./

# /data è il volume dove viene salvato/letto il token OAuth2
VOLUME ["/data"]

ENV MQTT_HOST=mosquitto
ENV MQTT_PORT=1883
ENV VIESSMANN_CLIENT_ID=viessmann.vicare.android
ENV POLL_INTERVAL_SECONDS=300

CMD ["python", "main.py"]
