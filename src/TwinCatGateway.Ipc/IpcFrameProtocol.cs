using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TwinCatGateway.Ipc;

[SuppressMessage(
    "Design",
    "CA1510:Use ArgumentNullException throw helper",
    Justification = "The shared implementation targets .NET Framework 4.8.")]
public static class IpcFrameProtocol
{
    public const int MaximumFrameBytes = 4 * 1024 * 1024;

    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task WriteAsync(
        Stream stream,
        string payload,
        CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        byte[] body = Utf8.GetBytes(payload);
        if (body.Length == 0 || body.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"IPC frame size must be between 1 and {MaximumFrameBytes} bytes.");
        }

        byte[] header =
        {
            (byte)body.Length,
            (byte)(body.Length >> 8),
            (byte)(body.Length >> 16),
            (byte)(body.Length >> 24),
        };
#if NET8_0_OR_GREATER
        await stream.WriteAsync(
            header,
            cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(
            body,
            cancellationToken).ConfigureAwait(false);
#else
        await stream.WriteAsync(
            header,
            0,
            header.Length,
            cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(
            body,
            0,
            body.Length,
            cancellationToken).ConfigureAwait(false);
#endif
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        byte[] header = new byte[sizeof(int)];
        await ReadExactlyAsync(
            stream,
            header,
            cancellationToken).ConfigureAwait(false);
        int length =
            header[0]
            | header[1] << 8
            | header[2] << 16
            | header[3] << 24;
        if (length <= 0 || length > MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"IPC frame size must be between 1 and {MaximumFrameBytes} bytes.");
        }

        byte[] body = new byte[length];
        await ReadExactlyAsync(
            stream,
            body,
            cancellationToken).ConfigureAwait(false);
        return Utf8.GetString(body);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
#if NET8_0_OR_GREATER
            int read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken).ConfigureAwait(false);
#else
            int read = await stream.ReadAsync(
                buffer,
                offset,
                buffer.Length - offset,
                cancellationToken).ConfigureAwait(false);
#endif
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "IPC stream ended before the complete frame was received.");
            }

            offset += read;
        }
    }
}
