using System.Collections.Generic;
using System.Windows.Forms;
using MonitorBateria.Models;
using PowerState = MonitorBateria.Models.PowerState;

namespace MonitorBateria.Services;

/// <summary>Bateria do próprio PC (notebooks/tablets). Em desktops não aparece.</summary>
public sealed class SystemBatteryProvider
{
    public List<DeviceReading> Read()
    {
        var list = new List<DeviceReading>();
        var ps = SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) ||
            ps.BatteryChargeStatus == BatteryChargeStatus.Unknown)
            return list;

        int level = (int)System.Math.Round(ps.BatteryLifePercent * 100);
        PowerState state;
        if (ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.Charging)) state = PowerState.Charging;
        else if (ps.PowerLineStatus == PowerLineStatus.Online) state = level >= 99 ? PowerState.Full : PowerState.Wired;
        else state = PowerState.Discharging;

        list.Add(new DeviceReading
        {
            Key = "sys:battery",
            Name = "Bateria do computador",
            Kind = DeviceKind.Laptop,
            Source = "Sistema",
            Level = level,
            IsConnected = true,
            ReportedState = state,
        });
        return list;
    }
}
