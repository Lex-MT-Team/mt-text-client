using System.Collections.Generic;
using FluentAssertions;
using MTTextClient.Core;
using MTTextClient.Tests.Infrastructure;
using Xunit;
using State = MTTextClient.Core.ConnectionStateObservable.ConnectionState;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// Stage 0.4 — in-process unit tests for
/// <see cref="ConnectionStateObservable"/>. No CoreConnection or MTCore
/// required; we drive Publish() directly and observe the events.
/// </summary>
[Trait("Category", TraitCategories.Unit)]
public sealed class ConnectionStateObservableUnitTests
{
    [Fact]
    public void Snapshot_ReturnsDisconnected_ForUnknownProfile()
    {
        var o = new ConnectionStateObservable();
        o.Snapshot("never_seen").Should().Be(State.Disconnected);
    }

    [Fact]
    public void Publish_RecordsState_AndFiresOnChange()
    {
        var o = new ConnectionStateObservable();
        var events = new List<ConnectionStateObservable.StateEvent>();
        o.OnStateChanged += e => events.Add(e);

        o.Publish("bench_01", State.Connecting);
        o.Publish("bench_01", State.Connected);

        events.Should().HaveCount(2);
        events[0].Profile.Should().Be("bench_01");
        events[0].State.Should().Be(State.Connecting);
        events[1].State.Should().Be(State.Connected);
        o.Snapshot("bench_01").Should().Be(State.Connected);
    }

    [Fact]
    public void Publish_IsIdempotent_NoEventWhenStateUnchanged()
    {
        var o = new ConnectionStateObservable();
        var events = new List<ConnectionStateObservable.StateEvent>();
        o.OnStateChanged += e => events.Add(e);

        o.Publish("bench_01", State.Connected);
        o.Publish("bench_01", State.Connected);   // identical — no event
        o.Publish("bench_01", State.Connected);   // identical — no event

        events.Should().HaveCount(1, because: "duplicate state publishes must not fire OnStateChanged");
    }

    [Fact]
    public void SnapshotAll_ReturnsEveryObservedProfile()
    {
        var o = new ConnectionStateObservable();
        o.Publish("bench_01", State.Connected);
        o.Publish("bench_02", State.Reconnecting);
        o.Publish("bench_03", State.Disconnected);

        var snap = o.SnapshotAll();
        snap.Should().HaveCount(3);
        snap["bench_01"].Should().Be(State.Connected);
        snap["bench_02"].Should().Be(State.Reconnecting);
        snap["bench_03"].Should().Be(State.Disconnected);
    }

    [Fact]
    public void Profile_IsCaseInsensitive()
    {
        var o = new ConnectionStateObservable();
        o.Publish("Bench_01", State.Connected);
        o.Snapshot("bench_01").Should().Be(State.Connected);
        o.Snapshot("BENCH_01").Should().Be(State.Connected);
    }

    [Fact]
    public void Publish_EmptyProfile_IsNoOp()
    {
        var o = new ConnectionStateObservable();
        var fired = 0;
        o.OnStateChanged += _ => fired++;
        o.Publish("", State.Connected);
        o.Publish(null!, State.Connected);
        fired.Should().Be(0);
    }

    [Fact]
    public void StateEvent_TimestampIsUtcAndReasonable()
    {
        var o = new ConnectionStateObservable();
        ConnectionStateObservable.StateEvent? captured = null;
        o.OnStateChanged += e => captured = e;
        o.Publish("bench_01", State.Connected);

        captured.Should().NotBeNull();
        captured!.At.Kind.Should().Be(System.DateTimeKind.Utc);
        captured.At.Should().BeCloseTo(System.DateTime.UtcNow, System.TimeSpan.FromSeconds(2));
    }
}
