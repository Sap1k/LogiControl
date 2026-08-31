// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Hid;

public static class HidOutputReportFormatter
{
    public const int MinimumUnnumberedLogitechReportLength = LogitechCommand.Length + 1;

    public static byte[] FormatUnnumberedCommand(
        LogitechCommand command,
        ushort outputReportByteLength)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (outputReportByteLength < MinimumUnnumberedLogitechReportLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputReportByteLength),
                outputReportByteLength,
                $"An unnumbered Logitech command needs at least {MinimumUnnumberedLogitechReportLength} bytes.");
        }

        var report = new byte[outputReportByteLength];
        command.Bytes.Span.CopyTo(report.AsSpan(1));
        return report;
    }
}
