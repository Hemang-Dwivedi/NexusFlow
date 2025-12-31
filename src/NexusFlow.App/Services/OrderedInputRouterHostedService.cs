using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Input;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Services;

public sealed class OrderedInputRouterHostedService : IHostedService
{
	private readonly IOrderedInputRouter _router;

	public OrderedInputRouterHostedService(IOrderedInputRouter router)
	{
		_router = router;
	}

	public Task StartAsync(CancellationToken cancellationToken)
		=> _router.StartAsync(cancellationToken);

	public Task StopAsync(CancellationToken cancellationToken)
		=> _router.StopAsync();
}
