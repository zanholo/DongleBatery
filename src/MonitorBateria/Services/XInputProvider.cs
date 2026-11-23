using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MonitorBateria.Models;

namespace MonitorBateria.Services;

/// <summary>
/// Controles Xbox / XInput (inclusive pelo adaptador sem fio Xbox, que não passa pelo Bluetooth).
/// O XInput só informa a bateria em 4 faixas, por isso o valor é aproximado.
/// </summary>
public sealed class XInputProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION { public byte BatteryType; public byte BatteryLevel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_CAPABILITIES
    {
        public byte Type, SubType; public ushort Flags;
        public ushort Buttons; public byte LeftTrigger, RightTrigger; public short ThumbLX, ThumbLY, ThumbRX, ThumbRY; // XINPUT_GAMEPAD
        public ushort LeftMotor, RightMotor;
    }

    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint user, byte devType, out XINPUT_BATTERY_INFORMATION info);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetCapabilities(uint user, uint flags, out XINPUT_CAPABILITIES caps);

    private const byte BATTERY_DEVTYPE_GAMEPAD = 0;
    private const byte BATTERY_TYPE_DISCONNECTED = 0, BATTERY_TYPE_WIRED = 1;
    private bool _unavailable;

    /// <param name="bluetoothGamepadsConnected">
    /// Quantidade de controles já detectados via Bluetooth com bateria; esses aparecem também no XInput
    /// e não devem ser duplicados.
    /// </param>
    public List<DeviceReading> Read(int bluetoothGamepadsConnected)
    {
        var list = new List<DeviceReading>();
        if (_unavailable) return list;
        int skip = bluetoothGamepadsConnected;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (XInputGetCapabilities(i, 0, out _) != 0) continue; // não conectado
                if (XInputGetBatteryInformation(i, BATTERY_DEVTYPE_GAMEPAD, out var bi) != 0) continue;
                if (bi.BatteryType == BATTERY_TYPE_DISCONNECTED) continue;

                bool wired = bi.BatteryType == BATTERY_TYPE_WIRED;
                if (!wired && skip > 0) { skip--; continue; }

                int? level = wired ? null : bi.BatteryLevel switch
                {
                    0 => 5,    // EMPTY
                    1 => 30,   // LOW
                    2 => 65,   // MEDIUM
                    _ => 100,  // FULL
                };

                list.Add(new DeviceReading
                {
                    Key = $"xinput:{i}",
                    Name = $"Controle Xbox (jogador {i + 1})",
                    Kind = DeviceKind.Gamepad,
                    Brand = "Microsoft",
                    Source = wired ? "USB" : "Xbox Wireless",
                    Level = level,
                    Approximate = !wired,
                    IsConnected = true,
                    ReportedState = wired ? PowerState.Wired : null,
                });
            }
        }
        catch (DllNotFoundException) { _unavailable = true; }
        catch (EntryPointNotFoundException) { _unavailable = true; }
        return list;
    }
}
