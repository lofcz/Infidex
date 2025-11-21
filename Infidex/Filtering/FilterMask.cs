using System.Collections;

namespace Infidex.Filtering;

/// <summary>
/// Efficient bit-based filter mask for document filtering.
/// Supports boolean operations (AND, OR, NOT).
/// </summary>
public class FilterMask
{
    private readonly BitArray _mask;
    private readonly int _capacity;
    
    public FilterMask(int capacity)
    {
        _capacity = capacity;
        _mask = new BitArray(capacity, false);
    }
    
    /// <summary>
    /// Checks if a document is in the filter
    /// </summary>
    public bool IsInFilter(int docIndex)
    {
        if (docIndex < 0 || docIndex >= _capacity)
            return false;
        
        return _mask[docIndex];
    }
    
    /// <summary>
    /// Adds a document to the filter
    /// </summary>
    public void Add(int docIndex)
    {
        if (docIndex >= 0 && docIndex < _capacity)
            _mask[docIndex] = true;
    }
    
    /// <summary>
    /// Removes a document from the filter
    /// </summary>
    public void Remove(int docIndex)
    {
        if (docIndex >= 0 && docIndex < _capacity)
            _mask[docIndex] = false;
    }
    
    /// <summary>
    /// Clears all documents from the filter
    /// </summary>
    public void Clear()
    {
        _mask.SetAll(false);
    }
    
    /// <summary>
    /// Performs AND operation with another mask
    /// </summary>
    public FilterMask And(FilterMask other)
    {
        FilterMask result = new FilterMask(Math.Min(_capacity, other._capacity));
        // Copy this mask and AND with other
        for (int i = 0; i < result._capacity; i++)
        {
            result._mask[i] = _mask[i] && other._mask[i];
        }
        return result;
    }
    
    /// <summary>
    /// Performs OR operation with another mask
    /// </summary>
    public FilterMask Or(FilterMask other)
    {
        FilterMask result = new FilterMask(Math.Min(_capacity, other._capacity));
        // Copy this mask and OR with other
        for (int i = 0; i < result._capacity; i++)
        {
            result._mask[i] = _mask[i] || other._mask[i];
        }
        return result;
    }
    
    /// <summary>
    /// Performs NOT operation
    /// </summary>
    public FilterMask Not()
    {
        FilterMask result = new FilterMask(_capacity);
        for (int i = 0; i < _capacity; i++)
        {
            result._mask[i] = !_mask[i];
        }
        return result;
    }
    
    /// <summary>
    /// Gets the number of documents in the filter
    /// </summary>
    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _capacity; i++)
            {
                if (_mask[i]) count++;
            }
            return count;
        }
    }
}

