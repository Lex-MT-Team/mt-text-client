using System.Linq;
using FluentAssertions;
using MTShared.Network;
using MTTextClient.MCP;
using Newtonsoft.Json;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// In-process coverage for the algorithm-profiling event bridge (PR #46):
/// the event ring-buffer capacity, the field-preserving serialization the
/// bridge depends on, and the shape of the published `algo_profiling` event.
/// No MTCore/network — pure in-process behavior.
/// </summary>
public sealed class ProfilingEventUnitTests
{
    // Pins the event ring-buffer capacity. A live profiling stream can burst
    // many events per second; the buffer was raised from 500 to 5000 so a burst
    // does not silently evict other cores' events before a client polls. This
    // test fails if the capacity is reverted toward the old 500.
    [Fact]
    [Trait("Category", "Unit")]
    public void EventBuffer_retains_a_full_burst_then_evicts_fifo_at_capacity()
    {
        const int capacity = 5000;
        var bus = new EventBroadcaster();

        for (int i = 0; i < capacity; i++)
            bus.Publish("algo_profiling", "core", new { i });

        var atCapacity = bus.GetSince(0);
        atCapacity.Count.Should().Be(capacity, "the buffer must hold a full burst without early eviction");
        atCapacity.First().Seq.Should().Be(1, "nothing is evicted at exactly capacity");

        // One past capacity → the buffer stays bounded and drops the oldest.
        bus.Publish("algo_profiling", "core", null);
        var overCapacity = bus.GetSince(0);
        overCapacity.Count.Should().Be(capacity, "the buffer is bounded at capacity");
        overCapacity.First().Seq.Should().Be(2, "the oldest event is evicted first (FIFO)");
    }

    // MTShared wire types (AlgorithmProfilingData, the live-algorithms result)
    // expose their data as public FIELDS, not properties. System.Text.Json
    // ignores fields by default and would emit "{}", silently dropping the
    // payload; Newtonsoft serializes the fields. This is why the profiling /
    // live-algorithms paths must use Newtonsoft. Guards against a regression
    // back to System.Text.Json.
    [Fact]
    [Trait("Category", "Unit")]
    public void Profiling_wire_type_fields_survive_newtonsoft_but_not_default_system_text_json()
    {
        var data = new AlgorithmProfilingData { algorithmID = 4242, algorithmName = "probe" };

        string newton = JsonConvert.SerializeObject(data);
        newton.Should().Contain("4242").And.Contain("probe",
            because: "Newtonsoft serializes the type's public fields");

        string systemTextJson = System.Text.Json.JsonSerializer.Serialize(data);
        systemTextJson.Should().NotContain("probe",
            because: "System.Text.Json ignores public fields by default — the reason the code uses Newtonsoft");
    }

    // The `algo_profiling` event carries the subscribed symbol alongside the
    // full profiling record, and serializes through the (Newtonsoft) event bus
    // without dropping either.
    [Fact]
    [Trait("Category", "Unit")]
    public void Algo_profiling_event_payload_carries_symbol_and_full_profiling_record()
    {
        var bus = new EventBroadcaster();
        var data = new AlgorithmProfilingData { algorithmID = 7, algorithmName = "sg" };

        bus.Publish("algo_profiling", "core-a", new { symbol = "btcusdt", profiling = data });

        var evt = bus.GetSince(0).Single();
        evt.Type.Should().Be("algo_profiling");
        evt.Core.Should().Be("core-a");

        string json = JsonConvert.SerializeObject(evt);
        json.Should().Contain("btcusdt", because: "the subscribed symbol is included in each event");
        json.Should().Contain("\"algorithmID\":7", because: "the profiling record is included in full");
    }
}
