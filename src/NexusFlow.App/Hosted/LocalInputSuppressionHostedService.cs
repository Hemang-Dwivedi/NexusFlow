using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Input;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Hosted;

/// <summary>
/// Connects the routing engine to WinHookCaptureService.ShouldRouteToRemote.
///
/// The delegate is evaluated on the hook thread at the exact moment of every
/// non-move input event — no flag, no race, no event subscription required.
///
/// When the delegate returns true the hook:
///   1. Raises the captured event so the orchestrator can forward it to the remote.
///   2. Returns non-zero to Windows, blocking delivery to any local application.
///
/// When false the hook passes the event through to local applications unchanged.
/// </summary>
public sealed class LocalInputSuppressionHostedService : IHostedService
{
    private const string Cat = "suppression";

    private readonly IWinHookCaptureService _hook;
    private readonly IRoutingEngine _routing;
    private readonly IFailsafeService _failsafe;
    private readonly IDiagnosticsLog _log;
    private readonly string _localPeerId;

    public LocalInputSuppressionHostedService(
        IWinHookCaptureService hook,
        IRoutingEngine routing,
        IFailsafeService failsafe,
        IDiagnosticsLog log,
        ILocalIdentity me)
    {
        _hook = hook;
        _routing = routing;
        _failsafe = failsafe;
        _log = log;
        _localPeerId = me.PeerId;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Wire the delegate once. The hook reads routing state fresh at every event.
        _hook.ShouldRouteToRemote = ShouldRouteToRemote;
        _log.Info(Cat, $"Input routing delegate installed. LocalPeerId={_localPeerId[..Math.Min(8, _localPeerId.Length)]}");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _hook.ShouldRouteToRemote = null;
        _log.Info(Cat, "Input routing delegate removed — all input passes through locally.");
        return Task.CompletedTask;
    }

    // Called on the hook thread at the moment of every non-move input event.
    private bool ShouldRouteToRemote()
        => _routing.ActiveTargetPeerId != _localPeerId && !_failsafe.IsBlocked;
}
