using BetterDns.Core.Configuration;

namespace BetterDns.Core.Routing;

public sealed class QueryLog
{
    private readonly object gate = new();
    private readonly Queue<QueryLogEntry> entries = new();
    private readonly int capacity;

    public QueryLog(int capacity = 500)
    {
        this.capacity = Math.Max(10, capacity);
    }

    public void Add(QueryLogEntry entry)
    {
        lock (gate)
        {
            entries.Enqueue(entry);
            while (entries.Count > capacity)
            {
                entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<QueryLogEntry> Snapshot(int count = 100)
    {
        lock (gate)
        {
            return entries.TakeLast(Math.Clamp(count, 1, capacity)).Reverse().ToArray();
        }
    }
}
