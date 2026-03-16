using NexusFlow.Core.Services;

namespace NexusFlow.Tests.Unit;

public class FailsafeServiceTests
{
    [Fact]
    public void InitialState_IsNotBlocked()
    {
        var svc = new FailsafeService();
        Assert.False(svc.IsBlocked);
    }

    [Fact]
    public void Block_SetsIsBlockedTrue()
    {
        var svc = new FailsafeService();
        svc.Block();
        Assert.True(svc.IsBlocked);
    }

    [Fact]
    public void Unblock_SetsIsBlockedFalse()
    {
        var svc = new FailsafeService();
        svc.Block();
        svc.Unblock();
        Assert.False(svc.IsBlocked);
    }

    [Fact]
    public void Toggle_FlipsFromUnblockedToBlocked()
    {
        var svc = new FailsafeService();
        svc.Toggle();
        Assert.True(svc.IsBlocked);
    }

    [Fact]
    public void Toggle_FlipsFromBlockedToUnblocked()
    {
        var svc = new FailsafeService();
        svc.Block();
        svc.Toggle();
        Assert.False(svc.IsBlocked);
    }

    [Fact]
    public void Block_WhenAlreadyBlocked_DoesNotFireChangedAgain()
    {
        var svc = new FailsafeService();
        int count = 0;
        svc.Changed += _ => count++;

        svc.Block();
        svc.Block(); // idempotent

        Assert.Equal(1, count);
    }

    [Fact]
    public void Unblock_WhenAlreadyUnblocked_DoesNotFireChanged()
    {
        var svc = new FailsafeService();
        int count = 0;
        svc.Changed += _ => count++;

        svc.Unblock(); // already not blocked

        Assert.Equal(0, count);
    }

    [Fact]
    public void Changed_ReceivesTrueOnBlock()
    {
        var svc = new FailsafeService();
        bool? received = null;
        svc.Changed += v => received = v;

        svc.Block();

        Assert.True(received);
    }

    [Fact]
    public void Changed_ReceivesFalseOnUnblock()
    {
        var svc = new FailsafeService();
        svc.Block();

        bool? received = null;
        svc.Changed += v => received = v;

        svc.Unblock();

        Assert.False(received);
    }
}
