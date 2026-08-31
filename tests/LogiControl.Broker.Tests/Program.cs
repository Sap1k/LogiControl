// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Broker;

if (!HasValidBoundaries(BrokerConstants.MaximumMessageBytes, BrokerConstants.IpcMajorVersion))
{
    Console.Error.WriteLine("FAIL Broker IPC boundary constants.");
    return 1;
}

Console.WriteLine("PASS Broker IPC boundary constants.");
return 0;

static bool HasValidBoundaries(int maximumMessageBytes, ushort majorVersion) =>
    maximumMessageBytes > 0 && majorVersion == 1;
