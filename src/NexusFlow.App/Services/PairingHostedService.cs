using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Trust;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Services;

public sealed class PairingHostedService : IHostedService
{
	private readonly PairingListener _listener;

	public PairingHostedService(PairingListener listener) => _listener = listener;

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_listener.Start();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
		=> _listener.StopAsync();
}
