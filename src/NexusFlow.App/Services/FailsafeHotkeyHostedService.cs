using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Services;
using NexusFlow.Input;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Services;

public sealed class FailsafeHotkeyHostedService : IHostedService
{
	private const string Cat = "failsafe.hotkey";

	private readonly GlobalHotkeyListener _hotkeys;
	private readonly IFailsafeService _failsafe;
	private readonly IDiagnosticsLog _log;

	public FailsafeHotkeyHostedService(GlobalHotkeyListener hotkeys, IFailsafeService failsafe, IDiagnosticsLog log)
	{
		_hotkeys = hotkeys;
		_failsafe = failsafe;
		_log = log;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_hotkeys.ShiftEscPressed += OnShiftEsc;
		_hotkeys.Start();

		_log.Info(Cat, "Global hotkey installed: Shift+Esc");
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_hotkeys.ShiftEscPressed -= OnShiftEsc;
		_hotkeys.Stop();

		_log.Info(Cat, "Global hotkey removed");
		return Task.CompletedTask;
	}

	private void OnShiftEsc()
	{
		// MUST be local-only and instant.
		_failsafe.Block();
		_log.Warn(Cat, "Shift+Esc pressed → FAILSAFE ON");
	}
}
