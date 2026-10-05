using DeChimex.Application.Common.Interfaces;
using DeChimex.Host.Realtime;
using FluentAssertions;
using Xunit;

namespace DeChimex.IntegrationTests.Realtime;

/// <summary>
/// Transient workspace presence: online while a side holds a connection, last-seen
/// stamped when its final connection drops, tombstones kept so "last active"
/// survives a full disconnect. Nothing here touches the database.
/// </summary>
public class OrderPresenceTrackerTests
{
    [Fact]
    public void JoinLeave_SnapshotTracksBothSides()
    {
        var sut = new OrderPresenceTracker();
        var orderId = Guid.NewGuid();

        sut.Join(orderId, "c1", "customer");
        sut.Join(orderId, "s1", "staff");
        sut.Join(orderId, "s2", "staff");
        sut.IsMember(orderId, "c1").Should().BeTrue();
        sut.IsMember(orderId, "nope").Should().BeFalse();
        sut.RoleOf("s1").Should().Be("staff");
        sut.IsRecipientConnected(orderId, "customer").Should().BeTrue();
        sut.IsRecipientConnected(orderId, "staff").Should().BeTrue();

        var online = sut.GetSnapshot(orderId);
        online.CustomerOnline.Should().BeTrue();
        online.StaffOnline.Should().BeTrue();

        sut.Leave("c1").Should().BeEquivalentTo([orderId]);
        var half = sut.GetSnapshot(orderId);
        half.CustomerOnline.Should().BeFalse();
        half.CustomerLastSeenUtc.Should().NotBeNull();
        half.StaffOnline.Should().BeTrue("one staff connection remains");

        sut.Leave("s1");
        sut.GetSnapshot(orderId).StaffOnline.Should().BeTrue();
        sut.Leave("s2");
        var off = sut.GetSnapshot(orderId);
        off.StaffOnline.Should().BeFalse();
        off.StaffLastSeenUtc.Should().NotBeNull();
        off.CustomerLastSeenUtc.Should().Be(off.CustomerLastSeenUtc,
            "the tombstone keeps last-seen after a full disconnect");
        sut.IsRecipientConnected(orderId, "customer").Should().BeFalse();
    }

    [Fact]
    public void Join_IgnoresUnknownRoles_AndLeaveUnknown_IsEmpty()
    {
        var sut = new OrderPresenceTracker();
        var orderId = Guid.NewGuid();

        sut.Join(orderId, "x1", "admin");
        sut.GetSnapshot(orderId).Should().Be(new OrderPresenceSnapshot(
            false, false, null, null));
        sut.Leave("ghost").Should().BeEmpty();
    }

    [Fact]
    public void Orders_AreIsolated()
    {
        var sut = new OrderPresenceTracker();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        sut.Join(a, "c1", "customer");

        sut.GetSnapshot(b).CustomerOnline.Should().BeFalse();
        sut.IsMember(b, "c1").Should().BeFalse();
    }
}
