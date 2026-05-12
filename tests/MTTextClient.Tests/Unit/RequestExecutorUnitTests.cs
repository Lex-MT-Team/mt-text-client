using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTTextClient.Core;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// Stage 0.4 — in-process unit tests for <see cref="RequestExecutor"/>.
/// No MTCore required; we drive a synthetic sender via callbacks.
///
/// Each test names exactly the property it asserts:
///   • <see cref="Execute_ReturnsResult_WhenCallbackFiresBeforeTimeout"/>
///   • <see cref="Execute_ReturnsNull_OnTimeout"/>
///   • <see cref="Execute_ReturnsNull_WhenSenderRejects"/>
///   • <see cref="Execute_ReturnsNull_WhenSenderThrows"/>
///   • <see cref="ExecuteWithFallback_InvokesFallback_OnTimeout"/>
///   • <see cref="ExecuteWithFallback_DoesNotInvokeFallback_OnSuccess"/>
///   • <see cref="ExecuteAsync_AwaitsResult"/>
/// </summary>
[Trait("Category", TraitCategories.Unit)]
public sealed class RequestExecutorUnitTests
{
    private sealed class Payload { public string Value { get; init; } = ""; }

    private static readonly RequestExecutor _exec = new();

    [Fact]
    public void Execute_ReturnsResult_WhenCallbackFiresBeforeTimeout()
    {
        Payload? result = _exec.Execute<Payload>(cb =>
        {
            // Fire the callback synchronously; in real use a UDP response
            // would come from a worker thread.
            cb(new Payload { Value = "ok" });
            return true;
        }, timeoutMs: 1000);

        result.Should().NotBeNull();
        result!.Value.Should().Be("ok");
    }

    [Fact]
    public void Execute_ReturnsNull_OnTimeout()
    {
        // Sender accepts but never fires the callback.
        Payload? result = _exec.Execute<Payload>(cb =>
        {
            return true;
        }, timeoutMs: 50);

        result.Should().BeNull(because: "no callback fired within the 50ms window");
    }

    [Fact]
    public void Execute_ReturnsNull_WhenSenderRejects()
    {
        Payload? result = _exec.Execute<Payload>(cb =>
        {
            return false;  // transport unavailable
        }, timeoutMs: 1000);

        result.Should().BeNull(because: "sender returned false (rejected)");
    }

    [Fact]
    public void Execute_ReturnsNull_WhenSenderThrows()
    {
        Payload? result = _exec.Execute<Payload>(cb =>
        {
            throw new InvalidOperationException("transport blew up");
        }, timeoutMs: 1000);

        result.Should().BeNull(because: "sender threw, the executor must swallow and return null");
    }

    [Fact]
    public void ExecuteWithFallback_InvokesFallback_OnTimeout()
    {
        bool fallbackCalled = false;
        Payload result = _exec.ExecuteWithFallback<Payload>(
            cb => true,                               // accepted, never fires
            timeoutMs: 50,
            fallback: () =>
            {
                fallbackCalled = true;
                return new Payload { Value = "empty" };
            });

        fallbackCalled.Should().BeTrue();
        result.Value.Should().Be("empty");
    }

    [Fact]
    public void ExecuteWithFallback_DoesNotInvokeFallback_OnSuccess()
    {
        bool fallbackCalled = false;
        Payload result = _exec.ExecuteWithFallback<Payload>(
            cb =>
            {
                cb(new Payload { Value = "real" });
                return true;
            },
            timeoutMs: 1000,
            fallback: () =>
            {
                fallbackCalled = true;
                return new Payload { Value = "empty" };
            });

        fallbackCalled.Should().BeFalse(because: "the real result arrived; fallback factory must not run");
        result.Value.Should().Be("real");
    }

    [Fact]
    public async Task ExecuteAsync_AwaitsResult()
    {
        Payload? result = await _exec.ExecuteAsync<Payload>(cb =>
        {
            // Fire callback from a worker thread after a short delay.
            _ = Task.Run(async () =>
            {
                await Task.Delay(10);
                cb(new Payload { Value = "async" });
            });
            return true;
        }, timeoutMs: 1000);

        result.Should().NotBeNull();
        result!.Value.Should().Be("async");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNull_OnTimeout()
    {
        Payload? result = await _exec.ExecuteAsync<Payload>(cb => true, timeoutMs: 50);
        result.Should().BeNull();
    }
}
