// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Hid;

public enum HidDeviceChange
{
    RescanRequired,
}

public interface IHidNotificationSource
{
    IAsyncEnumerable<HidDeviceChange> WatchAsync(
        CancellationToken cancellationToken = default);
}
