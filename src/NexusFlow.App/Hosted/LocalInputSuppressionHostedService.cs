using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Input;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Hosted;

/// <summary>
/// Bridges routing state into WinHookCaptureService suppression.
/// When the active target is a remote peer (and failsafe is not blocking),
/// local keyboard, mouse button, and mouse scroll events are suppressed
/// so they don't affect local applications while being sent to the remote.
/// Mouse moves always pass through so cursor tracking and boundary detection work.
/// </summary>
public sealed class LocalInputSuppressionHostedService : IHostedService
{
    private readonly IWinHookCaptureService _hook;
    private readonly IRoutingEngine _routing;
    private readonly IFailsafeService _failsafe;
    private readonly string _localPeerId;

    public LocalInputSuppressionHostedService(
        IWinHookCaptureService hook,
        IRoutingEngine routing,
        IFailsafeService failsafe,
        ILocalIdentity me)
    {
        _hook = hook;
        _routing = routing;
        _failsafe = failsafe;
        _localPeerId = me.PeerId;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _routing.ActiveTargetChanged += OnRoutingChanged;
        _failsafe.Changed += OnFailsafeChanged;
        Refresh();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _routing.ActiveTargetChanged -= OnRoutingChanged;
        _failsafe.Changed -= OnFailsafeChanged;
        _hook.SuppressLocalNonMoveInput = false;
        return Task.CompletedTask;
    }

    private void OnRoutingChanged(object? sender, string _) => Refresh();
    private void OnFailsafeChanged(bool _) => Refresh();

    private void Refresh()
    {
        var targetIsRemote = _routing.ActiveTargetPeerId != _localPeerId;
        var failsafeBlocked = _failsafe.IsBlocked;
        _hook.SuppressLocalNonMoveInput = targetIsRemote && !failsafeBlocked;
    }
}
