using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent;

/// <summary>
/// Keeps the most recent transcript events, bounded by age and count, so the
/// provider always sees a compact rolling view instead of the whole meeting.
/// </summary>
public sealed class RollingTranscriptWindow
{
    private readonly TimeSpan _maxAge;
    private readonly int _maxEvents;
    private readonly LinkedList<TranscriptEvent> _events = new();
    private readonly object _lock = new();

    public RollingTranscriptWindow(TimeSpan maxAge, int maxEvents)
    {
        _maxAge = maxAge;
        _maxEvents = maxEvents;
    }

    public void Add(TranscriptEvent e)
    {
        lock (_lock)
        {
            _events.AddLast(e);
            Trim(e.Timestamp);
        }
    }

    public IReadOnlyList<TranscriptEvent> Snapshot()
    {
        lock (_lock)
        {
            return _events.ToArray();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _events.Count;
            }
        }
    }

    private void Trim(DateTimeOffset newest)
    {
        while (_events.Count > _maxEvents)
        {
            _events.RemoveFirst();
        }

        while (_events.First is not null && newest - _events.First.Value.Timestamp > _maxAge)
        {
            _events.RemoveFirst();
        }
    }
}
