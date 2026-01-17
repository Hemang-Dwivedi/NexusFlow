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
	private readonly PairingListener _pairing;
	private readonly InputReceiver _inputReceiver;
	private readonly IHostApplicationLifetime _lifetime;

	public TcpMuxHostedService(
		TcpMuxHost host,
		InputReceiver inputReceiver,
		ConnectionManager connections,
		PairingListener pairing,
		IHostApplicationLifetime lifetime)
	{
		_host = host;
		_connections = connections;
		_pairing = pairing;
		_inputReceiver = inputReceiver;
		_lifetime = lifetime;
	}

	public Task StartAsync(CancellationToken ct)
	{
		_host.OnPairingFirstFrame = (client, stream, firstPayload) =>
			_pairing.HandleIncomingAsync(client, stream, firstPayload, _lifetime.ApplicationStopping);

		_host.OnControlFirstFrame = (client, stream, firstPayload) =>
			_connections.HandleIncomingAsync(client, stream, firstPayload, _lifetime.ApplicationStopping);

		_host.OnInputFirstFrame = (client, stream, firstPayload) =>
			_inputReceiver.HandleFirstFrameAsync(client, stream, firstPayload, _lifetime.ApplicationStopping);

		_host.Start();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct) => _host.StopAsync();
}
