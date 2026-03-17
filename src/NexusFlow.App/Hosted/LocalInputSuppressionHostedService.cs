using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Input;
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
    private readonly IWinHookCaptureService _hook;
    private readonly IRoutingEngine _routing;
    private readonly IFailsafeService _failsafe;
    private readonly string _localPeerId;

    // Track whether we currently have BlockInput active so we only
    // call the Win32 API when the state actually changes.
    private bool _blockInputActive;

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
        ApplySuppression(false);
        return Task.CompletedTask;
    }

    private void OnRoutingChanged(object? sender, string _) => Refresh();
    private void OnFailsafeChanged(bool _) => Refresh();

    private void Refresh()
    {
        var targetIsRemote = _routing.ActiveTargetPeerId != _localPeerId;
        var failsafeBlocked = _failsafe.IsBlocked;
        ApplySuppression(targetIsRemote && !failsafeBlocked);
    }

    private void ApplySuppression(bool suppress)
    {
        // Always update the hook flag first (fast path, no syscall needed).
        _hook.SuppressLocalNonMoveInput = suppress;

        // Only call BlockInput when the state changes to avoid redundant syscalls.
        if (suppress == _blockInputActive)
            return;

        _blockInputActive = suppress;
        BlockInput(suppress);
    }

    // ── Win32 ──────────────────────────────────────────────────────────────────
    // BlockInput blocks keyboard and mouse events from reaching applications.
    // Critically, low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) are NOT
    // affected — they still fire — so the failsafe hotkey and cursor tracking
    // continue to work normally while input is blocked.
    [DllImport("user32.dll")]
    private static extern bool BlockInput(bool fBlockIt);
}
