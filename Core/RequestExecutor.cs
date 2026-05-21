using System;
using System.Threading;
using System.Threading.Tasks;

namespace MTTextClient.Core;

/// <summary>
/// Generic request-execution helper.
///
/// The MTCore UDP request/response cycle in <see cref="CoreConnection"/>'s
/// per-request methods (RequestReports, RequestTicker24, RequestReportComments,
/// RequestReportDates, RequestAutoStopsReports, …) follows the same recipe:
///   1. check <c>_udpClient != null</c> (connection is up)
///   2. call <c>SendAndWait</c> with a per-request timeout
///   3. return <c>T?</c> — null on timeout, T on success
/// Some cluster sites translate null-on-timeout into <c>success:true, data:[]</c>
/// at the command-handler layer (the empty-array recipe).
///
/// <see cref="RequestExecutor"/> formalises the recipe so future request paths
/// can adopt a single seam instead of duplicating the pattern. It is a thin
/// wrapper, not a replacement for SendAndWait — SendAndWait owns the
/// circuit-breaker / rate-limiter interaction inside CoreConnection and stays
/// there.
///
/// Three operations are provided:
///
///   • <see cref="Execute{T}(Func{Action{T?}, bool}, int)"/>
///     — direct wrapper of a sender that yields T? on completion. Returns
///       the value or null on timeout.
///   • <see cref="ExecuteWithFallback{T}(Func{Action{T?}, bool}, int, Func{T})"/>
///     — same, but returns <c>fallback()</c> instead of null on timeout
///       (the empty-array recipe).
///   • <see cref="ExecuteAsync{T}(Func{Action{T?}, bool}, int)"/>
///     — async variant suitable for non-blocking dispatch.
///
/// Unit tests exercise these against a synthetic sender; no real MTCore
/// is required. That's the value: a single seam with deterministic
/// timeout behaviour, testable in milliseconds.
/// </summary>
public sealed class RequestExecutor
{
    /// <summary>
    /// Default timeout floor when callers pass <c>0</c>. Matches the
    /// SendAndWait default for "no explicit timeout" calls.
    /// </summary>
    public const int DefaultTimeoutMs = 30_000;

    /// <summary>
    /// Execute a request and wait for the callback to fire. Returns the
    /// received value or <c>null</c> if the timeout expires first.
    ///
    /// <paramref name="send"/> is invoked synchronously with a continuation
    /// callback; the callback may be invoked from any thread. The bool
    /// return value of <paramref name="send"/> indicates whether the send
    /// was accepted (i.e. the underlying transport is alive); a <c>false</c>
    /// return is propagated as <c>null</c> immediately without waiting.
    /// </summary>
    public T? Execute<T>(Func<Action<T?>, bool> send, int timeoutMs = DefaultTimeoutMs) where T : class
    {
        if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reg = cts.Token.Register(
            static state => ((TaskCompletionSource<T?>)state!).TrySetResult(null), tcs);

        bool accepted;
        try
        {
            accepted = send(result => tcs.TrySetResult(result));
        }
        catch
        {
            tcs.TrySetResult(null);
            accepted = false;
        }
        if (!accepted)
        {
            tcs.TrySetResult(null);
            return null;
        }

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// As <see cref="Execute{T}"/> but invokes <paramref name="fallback"/> on
    /// timeout instead of returning null. This is the empty-array recipe:
    /// cold/empty Firebird responds with silence, and the caller wants an
    /// empty result envelope (<c>success:true, data:[]</c>) rather than a
    /// timeout error. The fallback factory runs only on the timeout branch —
    /// fast path is unchanged.
    /// </summary>
    public T ExecuteWithFallback<T>(
        Func<Action<T?>, bool> send,
        int timeoutMs,
        Func<T> fallback) where T : class
    {
        T? result = Execute(send, timeoutMs);
        return result ?? fallback();
    }

    /// <summary>
    /// Command-layer overload. Wraps any synchronous null-returning getter
    /// with a fallback factory. Use this at command-layer call-sites
    /// (ReportsCommand, ExchangeCommand, AutoStopsCommand) to centralise
    /// the empty-result recipe without each handler open-coding the
    /// null-check + empty-envelope branch.
    ///
    /// The getter must already encapsulate its own timeout and accept-check
    /// (CoreConnection.RequestX methods do this via SendAndWait). Any
    /// exception thrown by the getter is treated as a timeout — the
    /// fallback is invoked and the exception swallowed. This matches the
    /// "silent on timeout" semantics callers already rely on.
    /// </summary>
    public T ExecuteWithFallback<T>(Func<T?> getter, Func<T> fallback) where T : class
    {
        try
        {
            T? result = getter();
            return result ?? fallback();
        }
        catch
        {
            return fallback();
        }
    }

    /// <summary>
    /// Async variant for callers that don't want the synchronous block.
    /// The same timeout / acceptance semantics apply.
    /// </summary>
    public Task<T?> ExecuteAsync<T>(Func<Action<T?>, bool> send, int timeoutMs = DefaultTimeoutMs) where T : class
    {
        if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => { tcs.TrySetResult(null); cts.Dispose(); });

        bool accepted;
        try
        {
            accepted = send(result =>
            {
                tcs.TrySetResult(result);
            });
        }
        catch
        {
            tcs.TrySetResult(null);
            return tcs.Task;
        }
        if (!accepted)
        {
            tcs.TrySetResult(null);
        }
        return tcs.Task;
    }
}
