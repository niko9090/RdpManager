namespace RdpmSync;

/// <summary>Conta i tentativi falliti per chiave in una finestra di tempo (in memoria).</summary>
public sealed class FailureLimiter(TimeProvider time, int max, TimeSpan window)
{
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new();
    private readonly object _lock = new();

    /// <summary>Se la chiave è bloccata, quanto manca allo sblocco; altrimenti null.</summary>
    public TimeSpan? Blocked(string key)
    {
        lock (_lock)
        {
            if (!_failures.TryGetValue(key, out var list))
                return null;
            Trim(list);
            if (list.Count < max)
                return null;
            var wait = list[0] + window - time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : null;
        }
    }

    public void Fail(string key)
    {
        lock (_lock)
        {
            if (_failures.Count > 10_000)
                foreach (var k in _failures.Keys.ToList())
                {
                    Trim(_failures[k]);
                    if (_failures[k].Count == 0)
                        _failures.Remove(k);
                }
            if (!_failures.TryGetValue(key, out var list))
                _failures[key] = list = [];
            Trim(list);
            list.Add(time.GetUtcNow());
        }
    }

    public void Reset(string key)
    {
        lock (_lock)
            _failures.Remove(key);
    }

    private void Trim(List<DateTimeOffset> list)
    {
        var limit = time.GetUtcNow() - window;
        list.RemoveAll(t => t <= limit);
    }
}
