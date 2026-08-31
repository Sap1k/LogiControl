// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Broker;

public static class BrokerConstants
{
    public const string PipeName = "LogiControl.Broker.v1";
    public const ushort IpcMajorVersion = 1;
    public const ushort IpcMinorVersion = 0;
    public const int MaximumMessageBytes = 64 * 1024;
}
