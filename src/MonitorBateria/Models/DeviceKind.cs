namespace MonitorBateria.Models;

public enum DeviceKind
{
    Generic,
    Mouse,
    Keyboard,
    Gamepad,
    Earbuds,
    Headphones,
    Headset,
    Speaker,
    Phone,
    Tablet,
    Pen,
    Watch,
    Touchpad,
    Remote,
    Laptop,
}

public enum PowerState
{
    Unknown,
    Charging,
    Discharging,
    Stable,
    Full,
    Wired,
}
