namespace CasaTimo.Core.Models;

public class SensorReading
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string Metric { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public string MqttTopic { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
