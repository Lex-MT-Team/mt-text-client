using System;
using System.Threading;
namespace MTTextClient.Core;

/// <summary>
/// MT-013 + MT-018: Multi-worker UDP polling pump for all CoreConnections.
///
/// Workers = min(4, ProcessorCount/2), each owning a stripe of connections.
/// Adaptive sleep per worker: targets ~8ms cycle regardless of connection count.
/// Fixes previous Math.Clamp(5,1,15) bug (value was always 5ms, never adaptive).
/// </summary>
public sealed class ConnectionPump : IDisposable
{
    // MT-018: scale workers with CPU, cap at 4
    private static readonly int WorkerCount =
        Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

    private readonly ConnectionManager _manager;
    private readonly Thread[] _workers;
    private volatile bool _running;
    private bool _disposed;

    private long _totalPolls;
    private long _totalErrors;

    public long TotalPolls  => Interlocked.Read(ref _totalPolls);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);
    public bool IsRunning   => _running;

    /// <summary>Number of pump worker threads configured.</summary>
    public int WorkerThreadCount => WorkerCount;

    public ConnectionPump(ConnectionManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _workers = new Thread[WorkerCount];
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        for (int w = 0; w < WorkerCount; w++)
        {
            int stripe = w; // capture for closure
            _workers[w] = new Thread(() => PumpLoop(stripe))
            {
                Name         = $"ConnectionPump-{stripe}",
                IsBackground = true,
                Priority     = ThreadPriority.AboveNormal
            };
            _workers[w].Start();
        }
    }

    public void Stop()
    {
        _running = false;
        foreach (Thread t in _workers)
            t?.Join(2000);
    }

    /// <summary>
    /// Each worker owns connections at indices: stripe, stripe+W, stripe+2W ...
    /// Even distribution without locking.
    ///
    /// MT-013 fix: sleep is derived from actual owned connection count,
    /// targeting an ~8ms poll cycle. Previously Math.Clamp(5,1,15) always
    /// returned 5 (the first arg was the value, not the range midpoint).
    /// </summary>
    private void PumpLoop(int stripe)
    {
        while (_running)
        {
            CoreConnection[] all = _manager.GetAllArray();
            int total = all.Length;

            if (total == 0)
            {
                Thread.Sleep(20);
                continue;
            }

            // Connections owned by this stripe
            int owned = (total + WorkerCount - 1) / WorkerCount; // ceiling div

            // Adaptive sleep: at 5 owned → 7ms, at 50 owned → 3ms, at 80+ → 1ms
            int sleepMs = Math.Clamp(8 - (owned / 10), 1, 10);

            for (int i = stripe; i < total; i += WorkerCount)
            {
                try
                {
                    all[i].PollEvents();
                    Interlocked.Increment(ref _totalPolls);
                }
                catch
                {
                    Interlocked.Increment(ref _totalErrors);
                }
            }

            Thread.Sleep(sleepMs);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
