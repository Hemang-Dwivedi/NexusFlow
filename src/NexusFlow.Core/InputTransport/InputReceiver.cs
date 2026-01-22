using System.Net.Sockets;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Protocol.Input;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;

namespace NexusFlow.Core.InputTransport;

public sealed class InputReceiver
{
	private const string Cat = "input-remote";
	private readonly IDiagnosticsLog _log;
	private readonly OrderedInputRouter _ordered;

	public InputReceiver(IDiagnosticsLog log, OrderedInputRouter ordered)
	{
		_log = log;
		_ordered = ordered;
	}

	public async Task HandleFirstFrameAsync(TcpClient client, NetworkStream stream, byte[] firstPayload, CancellationToken ct)
	{
		InputHelloV1 hello;
		try
		{
			hello = InputCodec.Decode<InputHelloV1>(firstPayload);
		}
		catch
		{
			client.Close();
			return;
		}

		_log.Info(Cat, $"RX input channel opened from {hello.FromPeerId}");

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var (type, payload) = await FramingV2.ReadAsync(stream, ct).ConfigureAwait(false);
				if (type != MessageType.Input)
					continue;

				var ev = InputCodec.Decode<InputEventV1>(payload);

				// RX trace (raw arrival)
				_log.Trace(Cat, $"RX {ev.FromPeerId} seq={ev.Seq} kind={ev.Kind}");

				// Ordered apply
				_ordered.Push(ev, ApplyInOrder);
			}
		}
		catch
		{
			// drop
		}
		finally
		{
			try { client.Close(); } catch { }
			_log.Info(Cat, $"RX input channel closed from {hello.FromPeerId}");
		}
	}

	private void ApplyInOrder(InputEventV1 ev)
	{
		// APPLY trace (this is the one that must be strictly in-order)
		switch (ev.Kind)
		{
			case InputKind.Key:
				_log.Trace(Cat, $"APPLY KEY vk={ev.Key!.VkCode} down={ev.Key.IsDown} seq={ev.Seq}");
				break;

			case InputKind.MouseMove:
				_log.Trace(Cat, $"APPLY MOVE dx={ev.Move!.Dx} dy={ev.Move.Dy} seq={ev.Seq}");
				break;

			case InputKind.MouseButton:
				_log.Trace(Cat, $"APPLY BTN {ev.Button!.Button} down={ev.Button.IsDown} seq={ev.Seq}");
				break;

			case InputKind.MouseWheel:
				_log.Trace(Cat, $"APPLY WHEEL delta={ev.Wheel!.Delta} seq={ev.Seq}");
				break;

			default:
				_log.Trace(Cat, $"APPLY {ev.FromPeerId} seq={ev.Seq} kind={ev.Kind}");
				break;
		}
	}
}
