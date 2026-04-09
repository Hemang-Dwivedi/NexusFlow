using System.Buffers;
using System.Buffers.Binary;

namespace NexusFlow.Protocol.Input;

/// <summary>
/// Zero-allocation binary codec for <see cref="InputEventV1"/>.
///
/// Wire format (little-endian):
///   [0]        InputKind   (1 byte)
///   [1..8]     Seq         (int64)
///   [9..16]    Timestamp   (int64)
///   [17..]     Payload     (varies by Kind):
///
///     MouseMove : Dx(4) + Dy(4)                    = 8 bytes  → total 25
///     Key       : VkCode(4) + ScanCode(4) + Down(1) = 9 bytes  → total 26
///     Button    : Button(1) + Down(1)               = 2 bytes  → total 19
///     Wheel     : Delta(4)                          = 4 bytes  → total 21
///
/// Compare with JSON ≈ 120–200 bytes per event.
///
/// Encode returns a RENTED ArrayPool buffer; the caller MUST return it via
/// <c>ArrayPool&lt;byte&gt;.Shared.Return(buffer)</c> after the write completes.
/// Decode reads from a <see cref="ReadOnlySpan{T}"/> with no heap allocations.
/// </summary>
public static class InputCodecBinary
{
	private const int HeaderSize   = 17; // Kind(1) + Seq(8) + Timestamp(8)
	private const int MaxEventSize = 32; // largest payload (Key=9) + header = 26; 32 has headroom

	/// <summary>
	/// Encode <paramref name="ev"/> into a rented byte buffer.
	/// Returns <c>(rentedBuffer, bytesWritten)</c>.
	/// <b>Caller must return <c>rentedBuffer</c> to <c>ArrayPool&lt;byte&gt;.Shared</c>.</b>
	/// </summary>
	public static (byte[] Buffer, int Length) Encode(in InputEventV1 ev)
	{
		var buf = ArrayPool<byte>.Shared.Rent(MaxEventSize);
		var pos = 0;

		buf[pos++] = (byte)ev.Kind;
		BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos, 8), ev.Seq);              pos += 8;
		BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos, 8), ev.TimestampUtcTicks); pos += 8;

		switch (ev.Kind)
		{
			case InputKind.MouseMove when ev.Move.HasValue:
			{
				var m = ev.Move.GetValueOrDefault();
				BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), m.Dx); pos += 4;
				BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), m.Dy); pos += 4;
				break;
			}
			case InputKind.Key when ev.Key.HasValue:
			{
				var k = ev.Key.GetValueOrDefault();
				BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), k.VkCode);   pos += 4;
				BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), k.ScanCode); pos += 4;
				buf[pos++] = k.IsDown ? (byte)1 : (byte)0;
				break;
			}
			case InputKind.MouseButton when ev.Button.HasValue:
			{
				var b = ev.Button.GetValueOrDefault();
				buf[pos++] = b.Button;
				buf[pos++] = b.IsDown ? (byte)1 : (byte)0;
				break;
			}
			case InputKind.MouseWheel when ev.Wheel.HasValue:
			{
				var w = ev.Wheel.GetValueOrDefault();
				BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), w.Delta); pos += 4;
				break;
			}
		}

		return (buf, pos);
	}

	/// <summary>
	/// Decode an <see cref="InputEventV1"/> from a binary span.
	/// Returns a struct by value — no heap allocations.
	/// <paramref name="fromPeerId"/> is the authenticated sender from the hello handshake.
	/// </summary>
	public static InputEventV1 Decode(ReadOnlySpan<byte> data, string fromPeerId)
	{
		if (data.Length < HeaderSize)
			throw new InvalidDataException($"Input frame too short: {data.Length} bytes.");

		var kind = (InputKind)data[0];
		var seq  = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(1, 8));
		var ts   = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(9, 8));
		var pl   = data.Slice(HeaderSize);

		return kind switch
		{
			InputKind.MouseMove => new InputEventV1(fromPeerId, seq, ts, kind,
				Move: new InputMouseMovePayload(
					Dx: BinaryPrimitives.ReadInt32LittleEndian(pl),
					Dy: BinaryPrimitives.ReadInt32LittleEndian(pl.Slice(4)),
					X: 0, Y: 0)),

			InputKind.Key => new InputEventV1(fromPeerId, seq, ts, kind,
				Key: new InputKeyPayload(
					VkCode:   BinaryPrimitives.ReadInt32LittleEndian(pl),
					ScanCode: BinaryPrimitives.ReadInt32LittleEndian(pl.Slice(4)),
					IsDown:   pl[8] != 0)),

			InputKind.MouseButton => new InputEventV1(fromPeerId, seq, ts, kind,
				Button: new InputMouseButtonPayload(
					Button: pl[0],
					IsDown: pl[1] != 0,
					X: 0, Y: 0)),

			InputKind.MouseWheel => new InputEventV1(fromPeerId, seq, ts, kind,
				Wheel: new InputMouseWheelPayload(
					Delta: BinaryPrimitives.ReadInt32LittleEndian(pl),
					X: 0, Y: 0)),

			_ => throw new InvalidDataException($"Unknown InputKind {kind}.")
		};
	}
}
