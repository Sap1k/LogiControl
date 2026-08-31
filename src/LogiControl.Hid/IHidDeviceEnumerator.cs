// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Hid;

public interface IHidDeviceEnumerator
{
    ValueTask<IReadOnlyList<HidDeviceSnapshot>> EnumerateAsync(
        CancellationToken cancellationToken = default);
}
