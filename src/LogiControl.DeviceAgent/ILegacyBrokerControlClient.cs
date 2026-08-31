// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.DeviceAgent;

public interface ILegacyBrokerControlClient : IAsyncDisposable
{
    ValueTask<LegacyBrokerStatus> AttachAsync(string devicePath, CancellationToken cancellationToken = default);

    ValueTask<LegacyBrokerStatus> ApplyProfileAsync(
        int rangeDegrees = 900,
        int overallGain = 10000,
        int boundaryForce = 3000,
        CancellationToken cancellationToken = default);

    ValueTask<LegacyBrokerStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    ValueTask<LegacyBrokerStatus> EmergencyStopAsync(CancellationToken cancellationToken = default);

    ValueTask<LegacyBrokerStatus> DetachAsync(CancellationToken cancellationToken = default);
}
