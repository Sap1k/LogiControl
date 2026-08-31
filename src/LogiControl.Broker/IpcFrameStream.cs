// SPDX-License-Identifier: GPL-3.0-or-later

using LogiControl.Protocol;

namespace LogiControl.Broker;

public readonly record struct IpcFrame(IpcFrameHeader Header, byte[] Payload);

public static class IpcFrameStream
{
    public static async ValueTask<IpcFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new byte[IpcFrameCodec.HeaderLength];
        int first = await stream.ReadAsync(headerBytes.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        await ReadExactlyAsync(stream, headerBytes.AsMemory(1), cancellationToken).ConfigureAwait(false);
        if (!IpcFrameCodec.TryReadHeader(headerBytes, out IpcFrameHeader header))
        {
            throw new InvalidDataException("Invalid semantic IPC header.");
        }

        var payload = new byte[header.PayloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return new IpcFrame(header, payload);
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        IpcFrameHeader header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length != header.PayloadLength)
        {
            throw new ArgumentException("Payload length does not match its header.", nameof(payload));
        }

        var headerBytes = new byte[IpcFrameCodec.HeaderLength];
        if (!IpcFrameCodec.TryWriteHeader(headerBytes, header))
        {
            throw new InvalidDataException("Response frame exceeds the protocol boundary.");
        }

        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            int read = await stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Semantic IPC frame ended before its declared length.");
            }

            destination = destination[read..];
        }
    }
}
