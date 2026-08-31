// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Win32.SafeHandles;

namespace LogiControl.Hid;

internal sealed class SafeDeviceInfoSetHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeDeviceInfoSetHandle(IntPtr handle)
        : base(true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.SetupDiDestroyDeviceInfoList(handle);
}
