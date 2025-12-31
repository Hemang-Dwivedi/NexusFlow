using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Input;
using NexusFlow.Core.Services;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Services;

public sealed class ModifierFailsafeHostedService : IHostedService
{
	private readonly IFailsafeService _failsafe;
	private readonly IModifierStateTracker _mods;

	public ModifierFailsafeHostedService(IFailsafeService failsafe, IModifierStateTracker mods)
	{
		_failsafe = failsafe;
		_mods = mods;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_failsafe.Changed += OnFailsafeChanged;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_failsafe.Changed -= OnFailsafeChanged;
		return Task.CompletedTask;
	}

	private void OnFailsafeChanged(bool blocked)
	{
		if (blocked)
			_mods.Reset("Failsafe ON");
	}
}
