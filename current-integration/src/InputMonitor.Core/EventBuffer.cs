namespace InputMonitor.Core;

public sealed class EventBuffer : IDisposable
{
    private readonly int _capacity;
    private readonly TimeSpan _flushInterval;
    private readonly object _gate;
    private readonly List<InputEventRecord> _buffer = [];
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>Invoked synchronously under the buffer's synchronization gate.</summary>
    public Action<IReadOnlyList<InputEventRecord>>? OnFlush { get; set; }

    public EventBuffer(int capacity = 500, TimeSpan? flushInterval = null)
        : this(new object(), capacity, flushInterval)
    {
    }

    internal EventBuffer(object synchronizationGate, int capacity = 500, TimeSpan? flushInterval = null)
    {
        _gate = synchronizationGate;
        _capacity = capacity;
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(5);
        _timer = new Timer(_ => Flush(), null, _flushInterval, _flushInterval);
    }

    public void Append(InputEventRecord record)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _buffer.Add(record);
            if (_buffer.Count >= _capacity)
            {
                FlushLocked();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                FlushLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
            FlushLocked();
        }
    }

    // Delivery shares the host's collection gate, so a batch detached by a timer
    // cannot be persisted after a later clear has already removed its data.
    private void FlushLocked()
    {
        var batch = DrainLocked();
        if (batch.Count > 0)
        {
            OnFlush?.Invoke(batch);
        }
    }

    private IReadOnlyList<InputEventRecord> DrainLocked()
    {
        if (_buffer.Count == 0)
        {
            return [];
        }

        var batch = _buffer.ToArray();
        _buffer.Clear();
        return batch;
    }
}
