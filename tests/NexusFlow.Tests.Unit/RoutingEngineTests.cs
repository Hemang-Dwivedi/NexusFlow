using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Protocol.Control;

namespace NexusFlow.Tests.Unit;

/// <summary>Minimal no-op fakes used across routing tests.</summary>
internal sealed class NoopBroadcaster : IControlBroadcaster
{
    public Task BroadcastAsync(object msg, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendToPeerAsync(string peerId, object msg, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NoopLog : IDiagnosticsLog
{
    public IReadOnlyList<LogEntry> Snapshot() => Array.Empty<LogEntry>();
    public event Action<LogEntry>? Added { add { } remove { } }
    public void Write(LogLevel level, string category, string message) { }
}

public class RoutingEngineTests
{
    private static RoutingEngine MakeEngine(
        string localPeerId = "peer-A",
        IControlBroadcaster? broadcaster = null,
        IFailsafeService? failsafe = null)
    {
        return new RoutingEngine(
            localPeerId,
            broadcaster ?? new NoopBroadcaster(),
            failsafe ?? new FailsafeService(),
            new NoopLog());
    }

    [Fact]
    public void InitialState_BothPointToLocalPeer()
    {
        var engine = MakeEngine("peer-A");
        Assert.Equal("peer-A", engine.ActiveTargetPeerId);
        Assert.Equal("peer-A", engine.ActiveSourcePeerId);
    }

    [Fact]
    public async Task RequestSetActiveTarget_ChangesTarget()
    {
        var engine = MakeEngine("peer-A");
        await engine.RequestSetActiveTargetAsync("peer-B");
        Assert.Equal("peer-B", engine.ActiveTargetPeerId);
    }

    [Fact]
    public async Task RequestSetActiveSource_ChangesSource()
    {
        var engine = MakeEngine("peer-A");
        await engine.RequestSetActiveSourceAsync("peer-B");
        Assert.Equal("peer-B", engine.ActiveSourcePeerId);
    }

    [Fact]
    public async Task RequestSetActiveTarget_FailsafeBlocked_IgnoresNonLocal()
    {
        var failsafe = new FailsafeService();
        var engine = MakeEngine("peer-A", failsafe: failsafe);
        failsafe.Block();

        await engine.RequestSetActiveTargetAsync("peer-B");

        Assert.Equal("peer-A", engine.ActiveTargetPeerId);
    }

    [Fact]
    public async Task RequestSetActiveSource_FailsafeBlocked_IgnoresNonLocal()
    {
        var failsafe = new FailsafeService();
        var engine = MakeEngine("peer-A", failsafe: failsafe);
        failsafe.Block();

        await engine.RequestSetActiveSourceAsync("peer-B");

        Assert.Equal("peer-A", engine.ActiveSourcePeerId);
    }

    [Fact]
    public void TryApplyRemoteV2_SetTarget_WithNewerStamp_IsApplied()
    {
        var engine = MakeEngine("peer-A");
        var stamp = new LamportStamp(999, "peer-X");
        var result = engine.TryApplyRemoteV2(new SetActiveTargetV2("peer-C", stamp));

        Assert.Equal(RoutingApplyDecision.Applied, result.Decision);
        Assert.Equal("peer-C", engine.ActiveTargetPeerId);
    }

    [Fact]
    public async Task TryApplyRemoteV2_SetTarget_WithOlderStamp_IsIgnored()
    {
        var engine = MakeEngine("peer-A");
        // First advance the local stamp by requesting a switch
        await engine.RequestSetActiveTargetAsync("peer-B");

        // Now try to apply an older (counter=0) stamp
        var oldStamp = new LamportStamp(0, "peer-X");
        var result = engine.TryApplyRemoteV2(new SetActiveTargetV2("peer-C", oldStamp));

        Assert.Equal(RoutingApplyDecision.Ignored_OlderStamp, result.Decision);
        // Target should remain peer-B, not peer-C
        Assert.Equal("peer-B", engine.ActiveTargetPeerId);
    }

    [Fact]
    public void TryApplyRemoteV2_FailsafeBlocked_IsIgnored()
    {
        var failsafe = new FailsafeService();
        var engine = MakeEngine("peer-A", failsafe: failsafe);
        failsafe.Block();

        var stamp = new LamportStamp(999, "peer-X");
        var result = engine.TryApplyRemoteV2(new SetActiveTargetV2("peer-C", stamp));

        Assert.Equal(RoutingApplyDecision.Ignored_FailsafeBlocked, result.Decision);
    }

    [Fact]
    public async Task HandlePeerDisconnected_TargetIsDisconnected_RevertsToSelf()
    {
        var engine = MakeEngine("peer-A");
        await engine.RequestSetActiveTargetAsync("peer-B");

        await engine.HandlePeerDisconnectedAsync("peer-B");

        Assert.Equal("peer-A", engine.ActiveTargetPeerId);
    }

    [Fact]
    public async Task HandlePeerDisconnected_SourceIsDisconnected_RevertsToSelf()
    {
        var engine = MakeEngine("peer-A");
        await engine.RequestSetActiveSourceAsync("peer-B");

        await engine.HandlePeerDisconnectedAsync("peer-B");

        Assert.Equal("peer-A", engine.ActiveSourcePeerId);
    }

    [Fact]
    public void LamportStamp_Compare_HigherCounterWins()
    {
        var low = new LamportStamp(1, "peer-A");
        var high = new LamportStamp(2, "peer-A");

        Assert.True(high.IsNewerThan(low));
        Assert.False(low.IsNewerThan(high));
    }

    [Fact]
    public void LamportStamp_Compare_TiebreakByPeerIdOrdinal()
    {
        var stampA = new LamportStamp(5, "peer-A");
        var stampB = new LamportStamp(5, "peer-B");

        // "peer-B" > "peer-A" ordinal => stampB is newer
        Assert.True(stampB.IsNewerThan(stampA));
        Assert.False(stampA.IsNewerThan(stampB));
    }

    [Fact]
    public async Task ActiveTargetChanged_FiredOnSwitch()
    {
        var engine = MakeEngine("peer-A");
        string? received = null;
        engine.ActiveTargetChanged += (_, id) => received = id;

        await engine.RequestSetActiveTargetAsync("peer-B");

        Assert.Equal("peer-B", received);
    }

    [Fact]
    public void GetSnapshotV2_ReturnsConsistentState()
    {
        var engine = MakeEngine("peer-A");

        var snap = engine.GetSnapshotV2();

        Assert.Equal("peer-A", snap.ActiveTargetPeerId);
        Assert.Equal("peer-A", snap.ActiveSourcePeerId);
    }
}
