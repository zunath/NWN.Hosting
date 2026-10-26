namespace Nwn.Hosting.Testing.Fixtures;

public sealed class TestObjectSelfScope : IDisposable
{
    private readonly Action<uint> _set;
    private readonly uint _previous;
    private int _disposed;

    public TestObjectSelfScope(uint value, Func<uint> get, Action<uint> set)
    {
        _set = set;
        _previous = get();
        set(value);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _set(_previous);
    }
}
