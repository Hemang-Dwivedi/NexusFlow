using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Input;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Hosted;

public sealed class LocalInputCaptureHostedService : IHostedService
{
	private readonly LocalInputCaptureOrchestrator _orch;

	public LocalInputCaptureHostedService(LocalInputCaptureOrchestrator orch)
	{
		_orch = orch;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_orch.Start();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_orch.Stop();
		return Task.CompletedTask;
	}
}
