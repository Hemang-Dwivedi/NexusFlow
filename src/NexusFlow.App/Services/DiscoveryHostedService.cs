using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Discovery;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Services;

public sealed class DiscoveryHostedService : IHostedService
{
	private readonly DiscoveryCoordinator _coordinator;

	public DiscoveryHostedService(DiscoveryCoordinator coordinator)
	{
		_coordinator = coordinator;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_coordinator.Start();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
		=> _coordinator.StopAsync();
}
