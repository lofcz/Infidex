namespace Infidex.Core;

/// <summary>
/// A heap-based storage for top-K search results.
/// </summary>
public class TopKHeap
{
    private readonly PriorityQueue<ScoreEntry, ScoreEntry> _heap;
    private readonly int _limit;

    public TopKHeap(int limit)
    {
        _limit = limit;
        _heap = new PriorityQueue<ScoreEntry, ScoreEntry>();
    }

    public int Count => _heap.Count;

    public void Add(ScoreEntry entry)
    {
        if (_heap.Count < _limit)
        {
            _heap.Enqueue(entry, entry);
        }
        else
        {
            ScoreEntry worst = _heap.Peek();
            if (entry.CompareTo(worst) > 0)
            {
                _heap.Dequeue();
                _heap.Enqueue(entry, entry);
            }
        }
    }
    
    public void Add(long documentId, float score, byte tiebreaker = 0, int? segmentNumber = null)
    {
        Add(new ScoreEntry(score, documentId, tiebreaker, segmentNumber));
    }

    public ScoreEntry[] GetTopK()
    {
        ScoreEntry[] result = new ScoreEntry[_heap.Count];
        for (int i = 0; i < result.Length; i++)
        {
            if (_heap.TryDequeue(out ScoreEntry entry, out _))
            {
                result[result.Length - 1 - i] = entry; 
            }
        }
        return result;
    }
    
    public void Clear()
    {
        _heap.Clear();
    }
}

