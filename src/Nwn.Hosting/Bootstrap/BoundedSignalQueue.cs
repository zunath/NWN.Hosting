namespace Nwn.Hosting.Bootstrap;

/// <summary>A thread-safe FIFO of NWNX signal names that refuses new entries once it holds <see cref="Capacity"/>.</summary>
public sealed class BoundedSignalQueue
{
    private readonly object _gate = new();
    private readonly Queue<string> _signals = new();

    public BoundedSignalQueue(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count { get { lock (_gate) return _signals.Count; } }

    public bool TryEnqueue(string signal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        lock (_gate)
        {
            if (_signals.Count >= Capacity)
            {
                return false;
            }
            _signals.Enqueue(signal);
            return true;
        }
    }

    public bool TryDequeue(out string signal)
    {
        lock (_gate)
        {
            if (_signals.Count == 0)
            {
                signal = string.Empty;
                return false;
            }
            signal = _signals.Dequeue();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) _signals.Clear();
    }
}
