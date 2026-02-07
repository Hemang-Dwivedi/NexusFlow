using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Routing;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Hosted;

public sealed class TargetSwitchingHostedService : IHostedService
{
	private readonly TargetSwitchingEngine _engine;

	public TargetSwitchingHostedService(TargetSwitchingEngine engine)
	{
		_engine = engine;
	}

	public Task StartAsync(CancellationToken ct)
	{
		// engine is event-driven; nothing to start
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct)
	{
		_engine.Dispose();
		return Task.CompletedTask;
	}
}
