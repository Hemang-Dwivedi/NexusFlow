using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Input;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Hosted;

/// <summary>
/// Bridges routing state into local input suppression.
///
/// Two complementary mechanisms are used together:
///
/// 1. BlockInput(TRUE) — Win32 API that prevents input events from reaching ANY
///    application on the desktop, including UAC-elevated processes.
///    WH_KEYBOARD_LL and WH_MOUSE_LL hooks still fire during BlockInput, so:
///      • ICursorTracker / TargetSwitchingEngine still receive mouse-move events.
///      • GlobalHotkeyListener still receives Shift+Esc → failsafe always works.
///
/// 2. WinHookCaptureService.SuppressLocalNonMoveInput — belt-and-suspenders hook
///    return value suppression (handles any edge case BlockInput misses).
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

    // Track whether we currently have BlockInput active so we only
    // call the Win32 API when the state actually changes.
    private bool _blockInputActive;

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
        ApplySuppression(false);
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
        ApplySuppression(shouldSuppress);
    }

    private void ApplySuppression(bool suppress)
    {
        // Always update the hook flag first (fast path, no syscall needed).
        _hook.SuppressLocalNonMoveInput = suppress;

        // Only call BlockInput when the state changes to avoid redundant syscalls.
        if (suppress == _blockInputActive)
            return;

        _blockInputActive = suppress;
        var ok = BlockInput(suppress);
        _log.Info(Cat, $"BlockInput({suppress}) => {(ok ? "OK" : "FAILED — check process elevation")}");
    }

    // ── Win32 ──────────────────────────────────────────────────────────────────
    // BlockInput blocks keyboard and mouse events from reaching applications.
    // Critically, low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) are NOT
    // affected — they still fire — so the failsafe hotkey and cursor tracking
    // continue to work normally while input is blocked.
    [DllImport("user32.dll")]
    private static extern bool BlockInput(bool fBlockIt);
}
