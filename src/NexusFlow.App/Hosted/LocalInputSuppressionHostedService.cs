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
/// Bridges routing state into WinHookCaptureService.SuppressLocalNonMoveInput.
///
/// When the active target is a remote peer (and failsafe is not blocking),
/// the WH_MOUSE_LL / WH_KEYBOARD_LL hook callbacks return a non-zero value,
/// which tells Windows to drop the event and not deliver it to any application.
///
/// Why NOT BlockInput():
///   BlockInput() works at the raw-input-thread level and prevents WH_MOUSE_LL
///   and WH_KEYBOARD_LL hooks from firing at all. That means captured events are
///   never forwarded to the remote peer, and the failsafe (Shift+Esc via
///   GlobalHotkeyListener's own WH_KEYBOARD_LL) cannot trigger.
///
/// Why the hook return value is sufficient (now that the process is elevated):
///   NexusFlow runs as Administrator (app.manifest requireAdministrator). At the
///   same privilege level as every other process, UIPI cannot block the hook's
///   return value. Windows respects the non-zero return from WH_MOUSE_LL /
///   WH_KEYBOARD_LL and drops the event before it reaches any window, regardless
///   of that window's own elevation.
///
/// Mouse moves always pass through so cursor tracking and boundary detection work.
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
        _routing.ActiveTargetChanged += OnRoutingChanged;
        _failsafe.Changed += OnFailsafeChanged;
        _log.Info(Cat, $"Service started. LocalPeerId={_localPeerId[..Math.Min(8, _localPeerId.Length)]}");
        Refresh();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _routing.ActiveTargetChanged -= OnRoutingChanged;
        _failsafe.Changed -= OnFailsafeChanged;
        _hook.SuppressLocalNonMoveInput = false;
        _log.Info(Cat, "Service stopped — suppression released.");
        return Task.CompletedTask;
    }

    private void OnRoutingChanged(object? sender, string newTarget)
    {
        _log.Info(Cat, $"ActiveTargetChanged -> {newTarget[..Math.Min(8, newTarget.Length)]} (local={_localPeerId[..Math.Min(8, _localPeerId.Length)]})");
        Refresh();
    }

    private void OnFailsafeChanged(bool blocked)
    {
        _log.Info(Cat, $"Failsafe changed -> {(blocked ? "BLOCKED" : "unblocked")}");
        Refresh();
    }

    private void Refresh()
    {
        var target = _routing.ActiveTargetPeerId;
        var targetIsRemote = target != _localPeerId;
        var failsafeBlocked = _failsafe.IsBlocked;
        var shouldSuppress = targetIsRemote && !failsafeBlocked;

        _log.Trace(Cat, $"Refresh: target={target[..Math.Min(8, target.Length)]} isRemote={targetIsRemote} failsafe={failsafeBlocked} => suppress={shouldSuppress}");
        _hook.SuppressLocalNonMoveInput = shouldSuppress;
    }
}
