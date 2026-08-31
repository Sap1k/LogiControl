// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Hid;

public interface IHidTransportFactory
{
    IHidTransport OpenForOutput(HidDeviceSnapshot device);
}

public sealed class WindowsHidTransportFactory : IHidTransportFactory
{
    public IHidTransport OpenForOutput(HidDeviceSnapshot device) =>
        WindowsHidTransport.OpenForOutput(device);
}
