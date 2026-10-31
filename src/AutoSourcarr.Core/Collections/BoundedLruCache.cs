namespace AutoSourcarr.Core.Collections;

public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly object _gate = new();

    public BoundedLruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Capacity => _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _items = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _order = [];

    public int Count { get { lock (_gate) return _items.Count; } }
    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_items.Remove(key, out var node)) return false;
            _order.Remove(node); return true;
        }
    }
    public void Clear() { lock (_gate) { _items.Clear(); _order.Clear(); } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var cached)) { value = default!; return false; }
            _order.Remove(cached);
            _order.AddFirst(cached);
            value = cached.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_items.Remove(key, out var old)) _order.Remove(old);
            _items.Add(key, _order.AddFirst((key, value)));
            Trim();
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> create)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var cached))
            {
                _order.Remove(cached);
                _order.AddFirst(cached);
                return cached.Value.Value;
            }
        }
        var value = create(key);
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var cached))
            {
                // Another caller created it first: its value wins and counts as a hit.
                _order.Remove(cached);
                _order.AddFirst(cached);
                return cached.Value.Value;
            }
            var node = _order.AddFirst((key, value));
            _items.Add(key, node);
            Trim();
        }
        return value;
    }

    private void Trim()
    {
        while (_items.Count > _capacity)
        {
            var last = _order.Last!;
            _items.Remove(last.Value.Key);
            _order.RemoveLast();
        }
    }
}
