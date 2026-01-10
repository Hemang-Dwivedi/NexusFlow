using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Control;
using NexusFlow.Core.InputTransport;
using NexusFlow.Core.Transport;
using NexusFlow.Core.Trust;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Services;

public sealed class TcpMuxHostedService : IHostedService
{
	private readonly TcpMuxHost _host;
	private readonly ConnectionManager _connections;
	private readonly PairingListener _pairing; // update PairingListener to expose HandleIncomingFirstFrame
	private readonly InputReceiver _inputReceiver;

	public TcpMuxHostedService(TcpMuxHost host, InputReceiver inputReceiver, ConnectionManager connections, PairingListener pairing)
	{
		_host = host;
		_connections = connections;
		_pairing = pairing;
		_inputReceiver = inputReceiver;
	}

	public Task StartAsync(CancellationToken ct)
	{
		_host.OnPairingFirstFrame = async (client, stream, firstPayload) =>
			await _pairing.HandleIncomingAsync(client, stream, firstPayload, ct);

		_host.OnControlFirstFrame = async (client, stream, firstPayload) =>
			await _connections.HandleIncomingAsync(client, stream, firstPayload, ct);


		_host.Start();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct) => _host.StopAsync();
}
