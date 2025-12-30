using System.Buffers.Binary;
using System.Net.Sockets;
using NexusFlow.Protocol.Transport;

namespace NexusFlow.Transport;

public static class FramingV2
{
	public static async Task WriteAsync(NetworkStream stream, MessageType type, byte[] payload, CancellationToken ct)
	{
		var header = new byte[5];
		header[0] = (byte)type;
		BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), payload.Length);

		await stream.WriteAsync(header.AsMemory(), ct);
		await stream.WriteAsync(payload.AsMemory(), ct);
		await stream.FlushAsync(ct);
	}

	public static async Task<(MessageType Type, byte[] Payload)> ReadAsync(NetworkStream stream, CancellationToken ct)
	{
		var header = await ReadExactAsync(stream, 5, ct);
		var type = (MessageType)header[0];
		var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
		if (len <= 0 || len > 1_000_000) throw new InvalidOperationException("Invalid frame length.");

		var payload = await ReadExactAsync(stream, len, ct);
		return (type, payload);
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
