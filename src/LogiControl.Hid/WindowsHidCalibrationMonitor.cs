// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LogiControl.Hid;

public sealed class WindowsHidCalibrationMonitor : IHidCalibrationMonitor
{
    private const int HidpInput = 0;
    private const int HidpStatusSuccess = 0x00110000;
    private static readonly TimeSpan StableCenterDuration = TimeSpan.FromMilliseconds(350);

    public async ValueTask<SteeringCalibrationObservation> WaitForCompletionAsync(
        HidDeviceSnapshot device,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.InputReportByteLength <= 0)
        {
            throw new InvalidDataException("The HID collection has no input report.");
        }

        using SafeFileHandle handle = NativeMethods.CreateFile(
            device.DevicePath,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Unable to open HID input for calibration on {device.InstanceId}.");
        }
        if (!NativeMethods.HidD_GetPreparsedData(handle, out IntPtr preparsedData))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "HidD_GetPreparsedData failed for calibration input.");
        }

        try
        {
            (uint logicalMinimum, uint logicalMaximum) = ReadSteeringLogicalBounds(preparsedData);
            await using var stream = new FileStream(
                handle,
                FileAccess.Read,
                device.InputReportByteLength,
                isAsync: true);
            var report = new byte[device.InputReportByteLength];
            var tracker = new SteeringCalibrationTracker(logicalMinimum, logicalMaximum);
            long startedAt = Stopwatch.GetTimestamp();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    TimeSpan now = Stopwatch.GetElapsedTime(startedAt);
                    if (tracker.IsComplete(now, StableCenterDuration)) break;

                    using var readSource = CancellationTokenSource.CreateLinkedTokenSource(
                        timeoutSource.Token);
                    Task<int> read = stream.ReadAsync(report, readSource.Token).AsTask();
                    TimeSpan remaining = tracker.StabilityRemaining(now, StableCenterDuration);
                    if (remaining != Timeout.InfiniteTimeSpan)
                    {
                        Task settled = Task.Delay(remaining, timeoutSource.Token);
                        if (await Task.WhenAny(read, settled).ConfigureAwait(false) == settled)
                        {
                            timeoutSource.Token.ThrowIfCancellationRequested();
                            readSource.Cancel();
                            try
                            {
                                await read.ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                                when (!timeoutSource.IsCancellationRequested)
                            {
                                // The wheel stayed centered for the entire quiet period.
                            }
                            break;
                        }
                    }

                    int bytesRead = await read.ConfigureAwait(false);
                    if (bytesRead != report.Length)
                    {
                        throw new InvalidDataException(
                            $"Calibration input length {bytesRead} did not match {report.Length}.");
                    }
                    int status = NativeMethods.HidP_GetUsageValue(
                        HidpInput,
                        0x01,
                        0,
                        0x30,
                        out uint x,
                        preparsedData,
                        report,
                        (uint)report.Length);
                    if (status != HidpStatusSuccess)
                    {
                        throw new InvalidDataException(
                            $"HidP_GetUsageValue failed with HID status 0x{status:X8}.");
                    }
                    tracker.Observe(x, Stopwatch.GetElapsedTime(startedAt));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SteeringCalibrationObservation observation = tracker.Snapshot();
                throw new TimeoutException(
                    $"Wheel calibration did not complete in {timeout.TotalSeconds:F0}s " +
                    $"(samples={observation.SampleCount}, min={observation.Minimum}, " +
                    $"max={observation.Maximum}, last={observation.Last}).");
            }

            return tracker.Snapshot();
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(preparsedData);
        }
    }

    private static (uint Minimum, uint Maximum) ReadSteeringLogicalBounds(IntPtr preparsedData)
    {
        int status = NativeMethods.HidP_GetCaps(preparsedData, out NativeMethods.HidpCaps capabilities);
        if (status != HidpStatusSuccess || capabilities.NumberInputValueCaps == 0)
        {
            throw new InvalidDataException($"HidP_GetCaps failed with HID status 0x{status:X8}.");
        }

        ushort count = capabilities.NumberInputValueCaps;
        var valueCaps = new NativeMethods.HidpValueCaps[count];
        status = NativeMethods.HidP_GetSpecificValueCaps(
            HidpInput,
            0x01,
            0,
            0x30,
            valueCaps,
            ref count,
            preparsedData);
        if (status != HidpStatusSuccess || count == 0)
        {
            throw new InvalidDataException(
                $"The HID descriptor does not expose a steering X-axis value capability (status 0x{status:X8}).");
        }

        NativeMethods.HidpValueCaps steering = valueCaps[0];
        if (steering.LogicalMinimum < 0 || steering.LogicalMaximum <= steering.LogicalMinimum)
        {
            throw new InvalidDataException(
                $"Unsupported steering logical bounds {steering.LogicalMinimum}..{steering.LogicalMaximum}.");
        }

        return ((uint)steering.LogicalMinimum, (uint)steering.LogicalMaximum);
    }
}
