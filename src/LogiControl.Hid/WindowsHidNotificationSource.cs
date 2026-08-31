// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LogiControl.Hid;

public sealed class WindowsHidNotificationSource : IHidNotificationSource
{
    public async IAsyncEnumerable<HidDeviceChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<HidDeviceChange>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);
        var filter = new NativeMethods.CmNotifyFilter
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CmNotifyFilter>(),
            FilterType = NativeMethods.CmNotifyFilterTypeDeviceInterface,
            ClassGuid = hidGuid,
            ReservedData = new byte[384],
        };

        NativeMethods.CmNotifyCallback callback = (_, _, _, _, _) =>
        {
            channel.Writer.TryWrite(HidDeviceChange.RescanRequired);
            return 0;
        };

        int result = NativeMethods.CmRegisterNotification(
            ref filter,
            IntPtr.Zero,
            callback,
            out IntPtr notification);
        if (result != NativeMethods.CrSuccess)
        {
            throw new InvalidOperationException($"CM_Register_Notification failed with configuration-manager code {result}.");
        }

        try
        {
            await foreach (HidDeviceChange change in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }
        finally
        {
            GC.KeepAlive(callback);
            NativeMethods.CmUnregisterNotification(notification);
            channel.Writer.TryComplete();
        }
    }
}
