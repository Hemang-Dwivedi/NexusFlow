using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Layout;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Input;

namespace NexusFlow.Tests.Unit;

/// <summary>Captures SetP0 calls for assertion; no-ops everything else.</summary>
internal sealed class FakeWinHookCaptureService : IWinHookCaptureService
{
    public event Action<CapturedKeyEvent>?         Key         { add { } remove { } }
    public event Action<CapturedMouseMoveEvent>?   MouseMove   { add { } remove { } }
    public event Action<CapturedMouseButtonEvent>? MouseButton { add { } remove { } }
    public event Action<CapturedMouseWheelEvent>?  MouseWheel  { add { } remove { } }
    public Func<bool>? ShouldRouteToRemote { get; set; }

    public (int X, int Y)? LastP0 { get; private set; }
    public void SetP0(int x, int y) => LastP0 = (x, y);

    public void Start() { }
    public void Stop()  { }
}

/// <summary>Fires the Moved event on demand.</summary>
internal sealed class FakeCursorTracker : ICursorTracker
{
    public event Action<int, int, int, int, long>? Moved;

    public void Fire(int x, int y, int dx, int dy)
        => Moved?.Invoke(x, y, dx, dy, DateTime.UtcNow.Ticks);

    public void FireAt(int x, int y, int dx, int dy, long ticks)
        => Moved?.Invoke(x, y, dx, dy, ticks);
}

/// <summary>ILayoutState backed by a fixed, replaceable snapshot.</summary>
internal sealed class FakeLayoutState : ILayoutState
{
    public LayoutSnapshot? Current { get; private set; }
    public event Action<LayoutSnapshot?>? Changed;

    public FakeLayoutState(LayoutSnapshot? snap = null) => Current = snap;

    public void Set(LayoutSnapshot? snap)
    {
        Current = snap;
        Changed?.Invoke(snap);
    }

    public void UpsertPeerRect(PeerRect peer) { }
    public void RemovePeer(string peerId) { }
}

/// <summary>Minimal ILocalIdentity returning a fixed PeerId.</summary>
internal sealed class FakeIdentity : ILocalIdentity
{
    public FakeIdentity(string peerId) { PeerId = peerId; }
    public string PeerId { get; }
    public string DeviceName => PeerId;
}

public class TargetSwitchingEngineTests
{
    private static LayoutSnapshot TwoPeerSnapshot()
    {
        // peer-A: x=0..1919, y=0..1079  |  peer-B: x=1920..3839, y=0..1079
        return new LayoutSnapshot(new[]
        {
            new PeerRect("peer-A", 0,    0, 1920, 1080),
            new PeerRect("peer-B", 1920, 0, 1920, 1080),
        });
    }

    private static (TargetSwitchingEngine engine, FakeCursorTracker cursor, FakeRoutingEngine routing, FailsafeService failsafe)
        MakeEngine(LayoutSnapshot? snap = null, string localPeerId = "peer-A")
    {
        var cursor   = new FakeCursorTracker();
        var routing  = new FakeRoutingEngine { ActiveTargetPeerId = localPeerId };
        var failsafe = new FailsafeService();
        var layout   = new FakeLayoutState(snap);
        var identity = new FakeIdentity(localPeerId);
        var capture  = new FakeWinHookCaptureService();
        var log      = new NoopLog();

        var engine = new TargetSwitchingEngine(identity, routing, failsafe, layout, cursor, capture, log);
        return (engine, cursor, routing, failsafe);
    }

    [Fact]
    public void CursorInsideLocal_NoSwitchAttempted()
    {
        var (engine, cursor, routing, _) = MakeEngine(TwoPeerSnapshot(), "peer-A");

        cursor.Fire(x: 960, y: 540, dx: 5, dy: 0); // well inside peer-A

        Assert.Empty(routing.SetTargetCalls);
    }

    [Fact]
    public void CursorExitsRightEdge_SwitchesToPeerB()
    {
        var (engine, cursor, routing, _) = MakeEngine(TwoPeerSnapshot(), "peer-A");

        // x=1930 is inside peer-B's rect; dx=20 shows rightward motion
        cursor.Fire(x: 1930, y: 540, dx: 20, dy: 0);

        Assert.Contains("peer-B", routing.SetTargetCalls);
    }

    [Fact]
    public void CursorExitsRightEdge_FailsafeBlocked_NoSwitch()
    {
        var (engine, cursor, routing, failsafe) = MakeEngine(TwoPeerSnapshot(), "peer-A");
        failsafe.Block();

        cursor.Fire(x: 1930, y: 540, dx: 20, dy: 0);

        Assert.Empty(routing.SetTargetCalls);
    }

    [Fact]
    public void NullSnapshot_NoSwitch()
    {
        var (engine, cursor, routing, _) = MakeEngine(snap: null, "peer-A");

        cursor.Fire(x: 1930, y: 540, dx: 20, dy: 0);

        Assert.Empty(routing.SetTargetCalls);
    }

    [Fact]
    public void CursorOverSelf_NoSwitch()
    {
        var (engine, cursor, routing, _) = MakeEngine(TwoPeerSnapshot(), "peer-A");

        // Position is still inside peer-A bounds
        cursor.Fire(x: 100, y: 100, dx: 5, dy: 0);

        Assert.Empty(routing.SetTargetCalls);
    }

    [Fact]
    public void Hysteresis_RapidFires_SwitchesOnlyOnce()
    {
        var (engine, cursor, routing, _) = MakeEngine(TwoPeerSnapshot(), "peer-A");

        // Fire many times within the 150ms cooldown using the same tick
        var ticks = DateTime.UtcNow.Ticks;
        for (int i = 0; i < 10; i++)
            cursor.FireAt(x: 1930, y: 540, dx: 20, dy: 0, ticks: ticks + i); // same ~tick group

        // Only one switch should have been issued
        Assert.Equal(1, routing.SetTargetCalls.Count);
    }

    [Fact]
    public void Enabled_False_NoSwitch()
    {
        var (engine, cursor, routing, _) = MakeEngine(TwoPeerSnapshot(), "peer-A");
        engine.Enabled = false;

        cursor.Fire(x: 1930, y: 540, dx: 20, dy: 0);

        Assert.Empty(routing.SetTargetCalls);
    }

    [Fact]
    public void Dispose_UnsubscribesFromCursor_NoMoreSwitches()
    {
        var (engine, cursor, routing, _) = MakeEngine(TwoPeerSnapshot(), "peer-A");
        engine.Dispose();

        cursor.Fire(x: 1930, y: 540, dx: 20, dy: 0);

        Assert.Empty(routing.SetTargetCalls);
    }
}
