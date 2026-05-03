using System;
using System.Threading;

namespace MTTextClient.Core;

/// <summary>
/// MT-021: Per-CoreConnection circuit breaker.
///
/// States:
///   Closed   → normal operation, calls pass through
///   Open     → fast-fail, no calls sent (Core is unresponsive)
///   HalfOpen → one probe call allowed; success → Closed, failure → Open
///
/// Transitions:
///   Closed   → Open:     when consecutive failures >= FailureThreshold
///   Open     → HalfOpen: after OpenDuration elapses
///   HalfOpen → Closed:   probe succeeds
///   HalfOpen → Open:     probe fails
///
/// Thread-safe via Interlocked + volatile.
/// </summary>
public sealed class CircuitBreaker
{
    public enum State { Closed, Open, HalfOpen }

    private readonly string _name;
    private readonly int    _failureThreshold;
    private readonly TimeSpan _openDuration;

    private State _state = State.Closed; // thread safety via Interlocked+Unsafe.As CAS
    private int  _consecutiveFailures;
    private long _openedAtTicks;              // Environment.TickCount64 when circuit opened
    private long _totalTripped;
    private long _totalRejected;

    public string Name             => _name;
    public State  CurrentState     => StateAtomic;
    public int    ConsecutiveFails => Volatile.Read(ref _consecutiveFailures);
    public long   TotalTripped     => Interlocked.Read(ref _totalTripped);
    public long   TotalRejected    => Interlocked.Read(ref _totalRejected);

    public bool IsOpen     => StateAtomic == State.Open;
    public bool IsClosed   => StateAtomic == State.Closed;
    public bool IsHalfOpen => StateAtomic == State.HalfOpen;


    // Atomic state I/O — _state is exchanged via Interlocked on its int alias.
    // Plain reads/writes of an enum field are NOT guaranteed visible across
    // threads on every CLR runtime; Volatile.Read / Interlocked.Exchange are.
    // EN review #6 / MCP-CB-001.
    private State StateAtomic =>
        (State)Volatile.Read(
            ref System.Runtime.CompilerServices.Unsafe.As<State, int>(ref _state));

    private void SetState(State value) =>
        Interlocked.Exchange(
            ref System.Runtime.CompilerServices.Unsafe.As<State, int>(ref _state),
            (int)value);

    public CircuitBreaker(string name, int failureThreshold = 5, int openDurationMs = 30_000)
    {
        _name             = name;
        _failureThreshold = failureThreshold;
        _openDuration     = TimeSpan.FromMilliseconds(openDurationMs);
    }

    /// <summary>
    /// Returns true if the call may proceed.
    /// False when circuit is Open (caller should fast-fail).
    /// In HalfOpen state, only the first caller gets through (probe).
    /// </summary>
    public bool AllowCall()
    {
        switch (StateAtomic)
        {
            case State.Closed:
                return true;

            case State.Open:
                long elapsed = Environment.TickCount64 - Interlocked.Read(ref _openedAtTicks);
                if (elapsed >= (long)_openDuration.TotalMilliseconds)
                {
                    // Transition to HalfOpen to allow one probe
                    var prev = (State)Interlocked.CompareExchange(
                        ref System.Runtime.CompilerServices.Unsafe.As<State, int>(ref _state),
                        (int)State.HalfOpen, (int)State.Open);
                    return prev == State.Open; // only the thread that won the CAS gets the probe
                }
                Interlocked.Increment(ref _totalRejected);
                return false;

            case State.HalfOpen:
                // Only one probe at a time
                Interlocked.Increment(ref _totalRejected);
                return false;

            default:
                return true;
        }
    }

    /// <summary>Record a successful call. Resets consecutive failures; closes the circuit.</summary>
    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        SetState(State.Closed);
    }

    /// <summary>Record a failed call (timeout, null response, exception).</summary>
    public void RecordFailure()
    {
        int fails = Interlocked.Increment(ref _consecutiveFailures);
        if (fails < _failureThreshold) return;

        // Race-free transition into Open: only the thread that wins the CAS
        // from {Closed | HalfOpen} → Open is allowed to bump _totalTripped.
        // Concurrent failures from many threads will collapse into a single
        // trip event instead of N spurious increments. EN review #6.
        ref int stateRef = ref System.Runtime.CompilerServices.Unsafe.As<State, int>(ref _state);

        int wonFromClosed = Interlocked.CompareExchange(
            ref stateRef, (int)State.Open, (int)State.Closed);
        if (wonFromClosed == (int)State.Closed)
        {
            Interlocked.Exchange(ref _openedAtTicks, Environment.TickCount64);
            Interlocked.Increment(ref _totalTripped);
            return;
        }

        int wonFromHalf = Interlocked.CompareExchange(
            ref stateRef, (int)State.Open, (int)State.HalfOpen);
        if (wonFromHalf == (int)State.HalfOpen)
        {
            Interlocked.Exchange(ref _openedAtTicks, Environment.TickCount64);
            Interlocked.Increment(ref _totalTripped);
        }
    }

    /// <summary>Manually reset the circuit to Closed (for operator override).</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        SetState(State.Closed);
    }

    public override string ToString() =>
        $"CircuitBreaker[{_name}] State={StateAtomic} Fails={_consecutiveFailures}/{_failureThreshold} Tripped={TotalTripped}";
}
