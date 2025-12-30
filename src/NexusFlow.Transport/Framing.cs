using System.Buffers.Binary;
using System.Net.Sockets;

namespace NexusFlow.Transport;

public static class Framing
{
    public static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
		var len = new byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
		await stream.WriteAsync(len.AsMemory(), ct);
		await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var lenBuf = await ReadExactAsync(stream, 4, ct);
        var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

        if (len <= 0 || len > 1_000_000) throw new InvalidOperationException("Invalid frame length.");

        return await ReadExactAsync(stream, len, ct);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var offset = 0;
        while (offset < n)
        {
            var read = await stream.ReadAsync(buf.AsMemory(offset, n - offset), ct);
            if (read == 0) throw new IOException("Socket closed.");
            offset += read;
        }
        return buf;
    }
}
