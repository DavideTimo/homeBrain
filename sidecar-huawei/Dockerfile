FROM python:3.12-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py .

ENV MQTT_HOST=localhost
ENV MQTT_PORT=1883
ENV POLL_INTERVAL_SECONDS=300

CMD ["python", "main.py"]
