using System.Net;
using System.Net.Sockets;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;

namespace NexusFlow.Core.Transport;

public sealed class TcpMuxHost : IDisposable
{
	private readonly int _port;
	private TcpListener? _listener;
	private CancellationTokenSource? _cts;
	private Task? _loop;

	public Func<TcpClient, NetworkStream, byte[] /*firstPayload*/, Task>? OnPairingFirstFrame { get; set; }
	public Func<TcpClient, NetworkStream, byte[] /*firstPayload*/, Task>? OnControlFirstFrame { get; set; }

	public TcpMuxHost(int port) => _port = port;

	public void Start()
	{
		if (_cts is not null) return;

		_cts = new CancellationTokenSource();
		_listener = new TcpListener(IPAddress.Any, _port);
		_listener.Start();
		_loop = AcceptLoopAsync(_cts.Token);
	}

	public async Task StopAsync()
	{
		if (_cts is null) return;
		_cts.Cancel();
		try { _listener?.Stop(); } catch { }
		try { if (_loop is not null) await _loop; } catch { }
		_cts.Dispose();
		_cts = null;
		_listener = null;
		_loop = null;
	}

	private async Task AcceptLoopAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			TcpClient client;
			try { client = await _listener!.AcceptTcpClientAsync(ct); }
			catch (OperationCanceledException) { break; }
			catch { continue; }

			_ = Task.Run(async () =>
			{
				var stream = client.GetStream();
				try
				{
					var (type, payload) = await FramingV2.ReadAsync(stream, ct);

					if (type == MessageType.Pairing && OnPairingFirstFrame is not null)
						await OnPairingFirstFrame(client, stream, payload);
					else if (type == MessageType.Control && OnControlFirstFrame is not null)
						await OnControlFirstFrame(client, stream, payload);
					else if (type == MessageType.Input && OnInputFirstFrame is not null)
						await OnInputFirstFrame(client, stream, payload);
					else
						client.Close();
				}
				catch
				{
					try { client.Close(); } catch { }
				}
			}, ct);
		}
	}
	public Func<TcpClient, NetworkStream, byte[] /*firstPayload*/, Task>? OnInputFirstFrame { get; set; }


	public void Dispose() => _ = StopAsync();
}
