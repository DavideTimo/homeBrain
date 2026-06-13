namespace CasaTimo.Core.Models;

public static class MqttTopics
{
    public const string PdcTempSupply = "casatimo/pdc/temperature/supply";
    public const string PdcTempReturn = "casatimo/pdc/temperature/return";
    public const string PdcPowerConsumption = "casatimo/pdc/power/consumption";
    public const string PdcDhwTemperature = "casatimo/pdc/dhw/temperature";
    public const string PdcMode = "casatimo/pdc/mode";
    public const string PdcCop = "casatimo/pdc/cop";
    public const string PdcOutdoorTemp = "casatimo/pdc/temperature/outdoor";

    public const string FvPowerProduction = "casatimo/fv/power/production";
    public const string FvEnergyToday = "casatimo/fv/energy/today";
    public const string FvBatterySoc = "casatimo/fv/battery/soc";
    public const string FvBatteryPower = "casatimo/fv/battery/power";
    public const string FvGridExport = "casatimo/fv/grid/export";
    public const string FvSelfConsumption = "casatimo/fv/self_consumption";

    public const string WallboxPower = "casatimo/wallbox/power";
    public const string WallboxSessionEnergy = "casatimo/wallbox/session/energy";
    public const string WallboxStatus = "casatimo/wallbox/status";

    public static string DaikinZoneTemp(int zone) => $"casatimo/daikin/zone/{zone}/temperature";

    public const string All = "casatimo/#";
}
