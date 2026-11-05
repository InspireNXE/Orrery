namespace Orrery.Protocols.Rdp;

/// <summary>
/// Holds the newest of a stream of values (pointer positions) until it's taken, so values that come faster than they're
/// sent are merged into one: only where the pointer has got to goes out. Not thread-safe; the caller locks.
/// </summary>
internal sealed class LatestOnly<T> where T : class
{
    T? _value;

    /// <summary>Keeps <paramref name="value"/> in place of any not yet taken. True when nothing was waiting, so a send needs queuing.</summary>
    public bool Offer(T value)
    {
        bool queue = _value is null;
        _value = value;
        return queue;
    }

    /// <summary>The newest value, now taken; null when there's none (it was taken already).</summary>
    public T? Take()
    {
        var value = _value;
        _value = null;
        return value;
    }
}
