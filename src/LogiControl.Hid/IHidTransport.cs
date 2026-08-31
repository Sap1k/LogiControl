// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Hid;

/// <summary>
/// Keeps feature/control-transfer output distinct from interrupt output so a caller
/// cannot accidentally change the transport used for mode switching.
/// </summary>
public interface IHidTransport : IAsyncDisposable
{
    HidDeviceSnapshot Device { get; }

    ValueTask SetOutputReportAsync(
        ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken = default);

    ValueTask WriteOutputReportAsync(
        ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken = default);
}
