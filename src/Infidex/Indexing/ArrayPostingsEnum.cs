using System;
using System.Collections.Generic;

namespace Infidex.Indexing;

/// <summary>
/// A PostingsEnum implementation backed by in-memory arrays/lists.
/// Used for testing and for in-memory index segments.
/// </summary>
public class ArrayPostingsEnum : IPostingsEnum
{
    private readonly IList<int> _docIds;
    private readonly IList<byte>? _freqs;
    private int _pos = -1;
    private int _docId = -1;

    public ArrayPostingsEnum(IList<int> docIds, IList<byte>? freqs)
    {
        _docIds = docIds;
        _freqs = freqs;
    }

    public int DocID => _docId;

    public float Freq
    {
        get
        {
            if (_pos >= 0 && _pos < _docIds.Count)
            {
                // If freqs provided, use them. Else assume 1.
                return _freqs != null ? _freqs[_pos] : 1f;
            }
            return 0f;
        }
    }

    public int NextDoc()
    {
        _pos++;
        if (_pos >= _docIds.Count)
        {
            _docId = PostingsEnumConstants.NO_MORE_DOCS;
            return PostingsEnumConstants.NO_MORE_DOCS;
        }
        _docId = _docIds[_pos];
        return _docId;
    }

    public int Advance(int target)
    {
        if (_docId == PostingsEnumConstants.NO_MORE_DOCS) return PostingsEnumConstants.NO_MORE_DOCS;
        if (target <= _docId)
        {
             return _docId; 
        }

        int start = _pos + 1;
        int count = _docIds.Count;
        if (start >= count)
        {
             _pos = count;
             _docId = PostingsEnumConstants.NO_MORE_DOCS;
             return PostingsEnumConstants.NO_MORE_DOCS;
        }

        // Exponential Search (Galloping)
        // Check 1, 2, 4, 8... ahead
        int limit = count - 1;
        int jump = 1;
        int high = start;
        
        // Optimizing for List<int> and int[] specifically to avoid virtual indexer calls in loop
        if (_docIds is List<int> list)
        {
            while (high <= limit && list[high] < target)
            {
                start = high + 1;
                high += jump;
                jump *= 2;
            }
            if (high > limit) high = limit;
            
            // Binary search in [start, high]
            if (start <= high)
            {
                int idx = list.BinarySearch(start, high - start + 1, target, null);
                if (idx < 0) idx = ~idx;
                _pos = idx;
            }
            else
            {
                _pos = start;
            }
        }
        else if (_docIds is int[] arr)
        {
            while (high <= limit && arr[high] < target)
            {
                start = high + 1;
                high += jump;
                jump *= 2;
            }
            if (high > limit) high = limit;
            
            if (start <= high)
            {
                int idx = Array.BinarySearch(arr, start, high - start + 1, target);
                if (idx < 0) idx = ~idx;
                _pos = idx;
            }
            else
            {
                _pos = start;
            }
        }
        else
        {
             // Linear scan fallback for generic IList
             while (start < count && _docIds[start] < target)
             {
                 start++;
             }
             _pos = start;
        }

        if (_pos >= count)
        {
            _docId = PostingsEnumConstants.NO_MORE_DOCS;
            return PostingsEnumConstants.NO_MORE_DOCS;
        }
        
        _docId = _docIds[_pos];
        return _docId;
    }

    public long Cost()
    {
        return _docIds.Count;
    }
}
