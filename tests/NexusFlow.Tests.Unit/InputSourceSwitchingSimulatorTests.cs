using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Input;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;

namespace NexusFlow.Tests.Unit;

/// <summary>Fake IRoutingEngine that records calls and returns a configurable ActiveSourcePeerId.</summary>
internal sealed class FakeRoutingEngine : IRoutingEngine
{
    public string ActiveSourcePeerId { get; set; } = "peer-A";
    public string ActiveTargetPeerId { get; set; } = "peer-A";
    public List<string> SetSourceCalls { get; } = new();
    public List<string> SetTargetCalls { get; } = new();

    public event EventHandler<string>? ActiveTargetChanged { add { } remove { } }
    public event EventHandler<string>? ActiveSourceChanged { add { } remove { } }

    public Task RequestSetActiveSourceAsync(string peerId, CancellationToken ct = default)
    {
        SetSourceCalls.Add(peerId);
        ActiveSourcePeerId = peerId;
        return Task.CompletedTask;
    }

    public Task RequestSetActiveTargetAsync(string peerId, CancellationToken ct = default)
    {
        SetTargetCalls.Add(peerId);
        ActiveTargetPeerId = peerId;
        return Task.CompletedTask;
    }

    public (string ActiveTargetPeerId, NexusFlow.Protocol.Control.LamportStamp TargetStamp,
            string ActiveSourcePeerId, NexusFlow.Protocol.Control.LamportStamp SourceStamp) GetSnapshotV2()
        => throw new NotSupportedException();

    public NexusFlow.Core.Routing.RoutingApplyResult TryApplyRemoteV2(object msg)
        => throw new NotSupportedException();

    public Task HandlePeerDisconnectedAsync(string peerId, CancellationToken ct = default)
        => Task.CompletedTask;
}

public class InputSourceSwitchingSimulatorTests
{
    private static (InputSourceSwitchingSimulator sim, FakeRoutingEngine routing, FailsafeService failsafe)
        MakeSim(double threshold = 12.0, string activePeerId = "peer-A")
    {
        var routing = new FakeRoutingEngine { ActiveSourcePeerId = activePeerId };
        var failsafe = new FailsafeService();
        var log = new NoopLog();
        var sim = new InputSourceSwitchingSimulator(routing, failsafe, log, threshold);
        return (sim, routing, failsafe);
    }

    [Fact]
    public async Task SimKeyPress_DifferentPeer_SwitchesImmediately()
    {
        var (sim, routing, _) = MakeSim(activePeerId: "peer-A");
        await sim.SimKeyPressAsync("peer-B");
        Assert.Contains("peer-B", routing.SetSourceCalls);
    }

    [Fact]
    public async Task SimKeyPress_SamePeer_DoesNotSwitch()
    {
        var (sim, routing, _) = MakeSim(activePeerId: "peer-A");
        await sim.SimKeyPressAsync("peer-A");
        Assert.Empty(routing.SetSourceCalls);
    }

    [Fact]
    public async Task SimMouseClick_DifferentPeer_SwitchesImmediately()
    {
        var (sim, routing, _) = MakeSim(activePeerId: "peer-A");
        await sim.SimMouseClickAsync("peer-B");
        Assert.Contains("peer-B", routing.SetSourceCalls);
    }

    [Fact]
    public async Task SimMouseScroll_DifferentPeer_SwitchesImmediately()
    {
        var (sim, routing, _) = MakeSim(activePeerId: "peer-A");
        await sim.SimMouseScrollAsync("peer-B");
        Assert.Contains("peer-B", routing.SetSourceCalls);
    }

    [Fact]
    public async Task SimMouseMove_BelowThreshold_DoesNotSwitch()
    {
        var (sim, routing, _) = MakeSim(threshold: 12.0, activePeerId: "peer-A");
        // magnitude = sqrt(3^2 + 4^2) = 5, below threshold of 12
        await sim.SimMouseMoveAsync("peer-B", dx: 3, dy: 4);
        Assert.Empty(routing.SetSourceCalls);
    }

    [Fact]
    public async Task SimMouseMove_AccumulatedAboveThreshold_Switches()
    {
        var (sim, routing, _) = MakeSim(threshold: 12.0, activePeerId: "peer-A");
        // First move: magnitude = sqrt(6^2 + 8^2) = 10, pending = 10
        await sim.SimMouseMoveAsync("peer-B", dx: 6, dy: 8);
        Assert.Empty(routing.SetSourceCalls);

        // Second move: magnitude = sqrt(3^2 + 4^2) = 5, pending = 15 > 12 → switch
        await sim.SimMouseMoveAsync("peer-B", dx: 3, dy: 4);
        Assert.Contains("peer-B", routing.SetSourceCalls);
    }

    [Fact]
    public async Task SimMouseMove_FromActivePeer_DoesNotSwitch()
    {
        var (sim, routing, _) = MakeSim(threshold: 1.0, activePeerId: "peer-A");
        // Large move but from the active peer — should never switch
        await sim.SimMouseMoveAsync("peer-A", dx: 100, dy: 0);
        Assert.Empty(routing.SetSourceCalls);
    }

    [Fact]
    public async Task SimMouseMove_FailsafeBlocked_DoesNotSwitch()
    {
        var (sim, routing, failsafe) = MakeSim(threshold: 1.0, activePeerId: "peer-A");
        failsafe.Block();

        await sim.SimMouseMoveAsync("peer-B", dx: 100, dy: 0);

        Assert.Empty(routing.SetSourceCalls);
    }

    [Fact]
    public async Task SimKeyPress_FailsafeBlocked_DoesNotSwitch()
    {
        var (sim, routing, failsafe) = MakeSim(activePeerId: "peer-A");
        failsafe.Block();

        await sim.SimKeyPressAsync("peer-B");

        Assert.Empty(routing.SetSourceCalls);
    }

    [Fact]
    public async Task SimMicActivity_NeverSwitches()
    {
        var (sim, routing, _) = MakeSim(activePeerId: "peer-A");
        await sim.SimMicActivityAsync("peer-B");
        Assert.Empty(routing.SetSourceCalls);
    }

    [Fact]
    public void MovementThresholdInfo_ContainsThresholdValue()
    {
        var (sim, _, _) = MakeSim(threshold: 15.5);
        Assert.Contains("15.5", sim.MovementThresholdInfo);
    }
}
