using System.Runtime.InteropServices;
using Infidex.Internalized.Roaring;

namespace Infidex.Indexing;

internal struct RoaringPostingsEnum : IPostingsEnum
{
    private readonly List<int> _docs;
    private int _idx;
    private int _currentDocId;
    private readonly float _fixedFreq;

    public RoaringPostingsEnum(RoaringBitmap bitmap, float fixedFreq = 1.0f)
    {
        _docs = bitmap.ToArray();
        _idx = -1;
        _currentDocId = -1;
        _fixedFreq = fixedFreq;
    }

    public int DocID => _currentDocId;

    public float Freq => _fixedFreq;

    public int NextDoc()
    {
        _idx++;

        if (_idx < _docs.Count)
        {
            _currentDocId = _docs[_idx];
            return _currentDocId;
        }
        _currentDocId = PostingsEnumConstants.NO_MORE_DOCS;
        return _currentDocId;
    }

    public int Advance(int target)
    {
        if (_currentDocId >= target && _currentDocId != PostingsEnumConstants.NO_MORE_DOCS)
            return _currentDocId;

        int count = _docs.Count;
        if (_idx + 1 >= count)
        {
            _currentDocId = PostingsEnumConstants.NO_MORE_DOCS;
            return _currentDocId;
        }

        // Fast path for sequential access
        if (_docs[_idx + 1] >= target)
        {
            _idx++;
            _currentDocId = _docs[_idx];
            return _currentDocId;
        }

        // Galloping Search on Span
        // Get span of remaining elements
        ReadOnlySpan<int> span = CollectionsMarshal.AsSpan(_docs);
        int start = _idx + 1;
        
        // Galloping (Exponential Search)
        int limit = count - 1;
        int offset = 1;
        int low = start;
        int high = start;

        // Find range [low, high] where target is
        while (high <= limit && span[high] < target)
        {
            low = high;
            high += offset;
            offset <<= 1;
        }
        if (high > limit) high = limit;

        // Binary search in [low, high]
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int midVal = span[mid];

            if (midVal < target)
                low = mid + 1;
            else if (midVal > target)
                high = mid - 1;
            else
            {
                _idx = mid;
                _currentDocId = midVal;
                return _currentDocId;
            }
        }

        // low is the insertion point (first element >= target)
        if (low < count)
        {
            _idx = low;
            _currentDocId = span[_idx];
        }
        else
        {
            _currentDocId = PostingsEnumConstants.NO_MORE_DOCS;
        }
        
        return _currentDocId;
    }

    public long Cost() => _docs.Count;
}
