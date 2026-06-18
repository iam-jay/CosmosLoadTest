namespace CosmosLoadTest;

// Thread-safe pool of (id, partitionKey) for documents known to exist.
// Read/Replace/Patch draw a random target; Delete removes one; Create/Upsert add.
public sealed class DocumentPool
{
    private readonly List<(string Id, string Pk)> _items = new();
    private readonly object _lock = new();
    private readonly Random _rng = new();

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public bool IsEmpty => Count == 0;

    public void Add(string id, string pk)
    {
        lock (_lock) _items.Add((id, pk));
    }

    // Random target WITHOUT removing (Read/Replace/Patch).
    public bool TryPeek(out string id, out string pk)
    {
        lock (_lock)
        {
            if (_items.Count == 0) { id = null; pk = null; return false; }
            var item = _items[_rng.Next(_items.Count)];
            id = item.Id; pk = item.Pk;
            return true;
        }
    }

    // Random target WITH removal (Delete). Swap-remove for O(1).
    public bool TryTake(out string id, out string pk)
    {
        lock (_lock)
        {
            if (_items.Count == 0) { id = null; pk = null; return false; }
            int idx = _rng.Next(_items.Count);
            var item = _items[idx];
            _items[idx] = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            id = item.Id; pk = item.Pk;
            return true;
        }
    }
}
