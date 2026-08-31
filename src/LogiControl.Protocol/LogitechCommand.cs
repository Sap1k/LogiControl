// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Protocol;

/// <summary>A single immutable seven-byte command understood by classic Logitech wheels.</summary>
public sealed class LogitechCommand
{
    public const int Length = 7;

    private readonly byte[] bytes;

    public LogitechCommand(params byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length != Length)
        {
            throw new ArgumentException($"A Logitech command must contain exactly {Length} bytes.", nameof(bytes));
        }

        this.bytes = (byte[])bytes.Clone();
    }

    public ReadOnlyMemory<byte> Bytes => bytes;

    public byte[] ToArray() => (byte[])bytes.Clone();
}
