namespace CasaTimo.Core.Models;

public class Device
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceType Type { get; set; }
    public string Location { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime LastSeen { get; set; }
}

public enum DeviceType
{
    HeatPump,
    Solar,
    Battery,
    Wallbox,
    Hvac,
    Ventilation,
    Other
}
