using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using NexusFlow.Protocol.Transport;

namespace NexusFlow.Transport;

public static class FramingV2
{
	// ── Write ────────────────────────────────────────────────────────────────

	/// <summary>
	/// Legacy write — allocates a new byte[] header each call.
	/// Used for one-off control/hello messages where alloc cost is irrelevant.
	/// </summary>
	public static async Task WriteAsync(NetworkStream stream, MessageType type, byte[] payload, CancellationToken ct)
	{
		var header = new byte[5];
		header[0] = (byte)type;
		BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), payload.Length);

		await stream.WriteAsync(header.AsMemory(), ct).ConfigureAwait(false);
		await stream.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Zero-alloc write for high-frequency input events.
	/// Combines the 5-byte framing header and the payload into a single pooled buffer
	/// and performs one <c>WriteAsync</c> instead of two, halving syscall overhead.
	/// <paramref name="payload"/> is a rented buffer; <paramref name="payloadLen"/> is
	/// the number of valid bytes within it.
	/// </summary>
	public static async Task WritePooledAsync(
		NetworkStream stream, MessageType type,
		byte[] payload, int payloadLen,
		CancellationToken ct)
	{
		var totalLen = 5 + payloadLen;
		var buf = ArrayPool<byte>.Shared.Rent(totalLen);
		try
		{
			buf[0] = (byte)type;
			BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1, 4), payloadLen);
			payload.AsSpan(0, payloadLen).CopyTo(buf.AsSpan(5));

			await stream.WriteAsync(buf.AsMemory(0, totalLen), ct).ConfigureAwait(false);
			await stream.FlushAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buf);
		}
	}

	// ── Read ─────────────────────────────────────────────────────────────────

	/// <summary>
	/// Legacy read — allocates new byte[] buffers for the header and payload.
	/// Used for one-off hello frames where alloc cost is irrelevant.
	/// </summary>
	public static async Task<(MessageType Type, byte[] Payload)> ReadAsync(NetworkStream stream, CancellationToken ct)
	{
		var header = await ReadExactAsync(stream, 5, ct).ConfigureAwait(false);
		var type   = (MessageType)header[0];
		var len    = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
		if (len <= 0 || len > 1_000_000) throw new InvalidOperationException("Invalid frame length.");

		var payload = await ReadExactAsync(stream, len, ct).ConfigureAwait(false);
		return (type, payload);
	}

	/// <summary>
	/// Zero-alloc read for high-frequency input events.
	/// Returns a RENTED payload buffer from <see cref="ArrayPool{T}"/>.
	/// <b>Caller MUST return it via <c>ArrayPool&lt;byte&gt;.Shared.Return(rentedPayload)</c>.</b>
	/// </summary>
	public static async Task<(MessageType Type, byte[] RentedPayload, int Length)> ReadPooledAsync(
		NetworkStream stream, CancellationToken ct)
	{
		// 5-byte header via pool — returned before this method yields control again.
		var header = ArrayPool<byte>.Shared.Rent(5);
		int len;
		MessageType type;
		try
		{
			await ReadExactIntoAsync(stream, header, 5, ct).ConfigureAwait(false);
			type = (MessageType)header[0];
			len  = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
			if (len <= 0 || len > 1_000_000) throw new InvalidOperationException("Invalid frame length.");
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(header);
		}

		var payload = ArrayPool<byte>.Shared.Rent(len);
		try
		{
			await ReadExactIntoAsync(stream, payload, len, ct).ConfigureAwait(false);
			return (type, payload, len);
		}
		catch
		{
			ArrayPool<byte>.Shared.Return(payload);
			throw;
		}
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int n, CancellationToken ct)
	{
		var buf    = new byte[n];
		var offset = 0;
		while (offset < n)
		{
			var read = await stream.ReadAsync(buf.AsMemory(offset, n - offset), ct).ConfigureAwait(false);
			if (read == 0) throw new IOException("Socket closed.");
			offset += read;
		}
		return buf;
	}

	private static async Task ReadExactIntoAsync(NetworkStream stream, byte[] buf, int n, CancellationToken ct)
	{
		var offset = 0;
		while (offset < n)
		{
			var read = await stream.ReadAsync(buf.AsMemory(offset, n - offset), ct).ConfigureAwait(false);
			if (read == 0) throw new IOException("Socket closed.");
			offset += read;
		}
	}
}
