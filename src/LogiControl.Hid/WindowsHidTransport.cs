// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LogiControl.Hid;

public sealed class WindowsHidTransport : IHidTransport
{
    private readonly SafeFileHandle handle;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private bool disposed;

    private WindowsHidTransport(HidDeviceSnapshot device, SafeFileHandle handle)
    {
        Device = device;
        this.handle = handle;
    }

    public HidDeviceSnapshot Device { get; }

    public static WindowsHidTransport OpenForOutput(HidDeviceSnapshot device)
    {
        ArgumentNullException.ThrowIfNull(device);
        SafeFileHandle handle = NativeMethods.CreateFile(
            device.DevicePath,
            NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Unable to open HID output for {device.InstanceId}.");
        }

        return new WindowsHidTransport(device, handle);
    }

    public async ValueTask SetOutputReportAsync(
        ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken = default)
    {
        byte[] buffer = ValidateAndCopy(report);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!NativeMethods.HidD_SetOutputReport(handle, buffer, (uint)buffer.Length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_SetOutputReport failed.");
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask WriteOutputReportAsync(
        ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken = default)
    {
        byte[] buffer = ValidateAndCopy(report);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!NativeMethods.WriteFile(handle, buffer, (uint)buffer.Length, out uint written, IntPtr.Zero) ||
                written != buffer.Length)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteFile HID output failed.");
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            handle.Dispose();
        }
        finally
        {
            writeLock.Release();
            writeLock.Dispose();
        }
    }

    private byte[] ValidateAndCopy(ReadOnlyMemory<byte> report)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (report.Length != Device.OutputReportByteLength)
        {
            throw new ArgumentException(
                $"Report length {report.Length} does not match the HID collection length {Device.OutputReportByteLength}.",
                nameof(report));
        }

        return report.ToArray();
    }
}
