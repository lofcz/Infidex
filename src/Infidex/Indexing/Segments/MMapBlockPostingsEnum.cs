using Infidex.Indexing.Compression;
using System.Runtime.CompilerServices;

namespace Infidex.Indexing.Segments;

internal unsafe struct MMapBlockPostingsEnum : IPostingsEnum
{
    private readonly byte* _basePtr;
    private readonly int _totalCount;
    private readonly int _numBlocks;
    
    // Skip Table (Cached in RAM)
    // For struct, we hold references to arrays.
    private readonly int[] _minDocs;
    private readonly int[] _maxDocs;
    private readonly long[] _blockOffsets;
    private readonly byte[] _maxWeights; 
    
    // Offset
    private readonly int _baseDocId;

    // Current Block State
    private int _currentBlockIndex; // Initialized to -1 in constructor
    private int _docBufferCount;
    private int _docBufferIndex;
    private readonly int[] _docBuffer; // Shared buffer? No, struct field.
    private readonly byte[] _weightBuffer;
    
    private bool _blockLoaded;
    private int _docId;
    private float _freq;

    // Instrumentation
    public int AdvanceCount;
    public int NextDocCount;

    public MMapBlockPostingsEnum(byte* ptr, long offset, int baseDocId = 0)
    {
        _baseDocId = baseDocId;
        _docBuffer = new int[BlockPostingsWriter.BlockSize];
        _weightBuffer = new byte[BlockPostingsWriter.BlockSize];
        _currentBlockIndex = -1;
        _docBufferIndex = 0;
        _docBufferCount = 0;
        _blockLoaded = false;
        _docId = -1;
        _freq = 0;
        AdvanceCount = 0;
        NextDocCount = 0;

        // Read Header from ptr + offset
        byte* p = ptr + offset;
        _basePtr = ptr; 
        
        _totalCount = *(int*)p; p += 4;
        
        if (_totalCount > 0)
        {
            _numBlocks = *(int*)p; p += 4;
            long skipTableOffset = *(long*)p; p += 8;
            
            // Read Skip Table into RAM
            byte* skipTablePtr = ptr + skipTableOffset;
            
            _minDocs = new int[_numBlocks];
            _maxDocs = new int[_numBlocks];
            _blockOffsets = new long[_numBlocks];
            _maxWeights = new byte[_numBlocks];
            
            byte* sp = skipTablePtr;
            for(int i=0; i<_numBlocks; i++)
            {
                _minDocs[i] = *(int*)sp; sp += 4;
                _maxDocs[i] = *(int*)sp; sp += 4;
                _blockOffsets[i] = *(long*)sp; sp += 8;
                _maxWeights[i] = *sp; sp += 1;
            }
        }
        else
        {
            _numBlocks = 0;
            _minDocs = Array.Empty<int>();
            _maxDocs = Array.Empty<int>();
            _blockOffsets = Array.Empty<long>();
            _maxWeights = Array.Empty<byte>();
        }
    }

    public int DocID => _docId == -1 || _docId == PostingsEnumConstants.NO_MORE_DOCS ? _docId : _docId + _baseDocId;
    
    public float Freq
    {
        get
        {
            if (!_blockLoaded)
            {
                if (_currentBlockIndex >= 0)
                    LoadBlock(_currentBlockIndex);
                else
                    return 0; // Should not happen if docId valid
            }
            return _freq;
        }
    }
    
    public int GetBlockMaxWeight(int blockIndex)
    {
        if (blockIndex >= 0 && blockIndex < _numBlocks)
            return _maxWeights[blockIndex];
        return 0;
    }

    public int NextDoc()
    {
        NextDocCount++;
        // Ensure block is loaded if we are iterating
        if (!_blockLoaded && _currentBlockIndex >= 0)
        {
            LoadBlock(_currentBlockIndex);
        }

        if (_docBufferIndex >= _docBufferCount)
        {
            if (!LoadNextBlock())
            {
                _docId = PostingsEnumConstants.NO_MORE_DOCS;
                return PostingsEnumConstants.NO_MORE_DOCS;
            }
        }

        _docId = _docBuffer[_docBufferIndex];
        _freq = _weightBuffer[_docBufferIndex];
        _docBufferIndex++;
        return _docId + _baseDocId;
    }

    public int Advance(int target)
    {
        AdvanceCount++;
        int adjustedTarget = target - _baseDocId;
        if (adjustedTarget < 0) adjustedTarget = 0; // Target before start of segment

        if (_docId != PostingsEnumConstants.NO_MORE_DOCS && adjustedTarget <= _docId)
            return _docId + _baseDocId;

        // Check current block max doc first
        if (_currentBlockIndex >= 0 && _currentBlockIndex < _numBlocks)
        {
             int maxDoc = _maxDocs[_currentBlockIndex];
             if (adjustedTarget <= maxDoc)
             {
                 // Target in current block.
                 // Ensure loaded
                 if (!_blockLoaded) LoadBlock(_currentBlockIndex);
                 
                 // Scan
                 while (_docBufferIndex < _docBufferCount)
                 {
                     if (_docBuffer[_docBufferIndex] >= adjustedTarget)
                     {
                         _docId = _docBuffer[_docBufferIndex];
                         _freq = _weightBuffer[_docBufferIndex];
                         _docBufferIndex++;
                         return _docId + _baseDocId;
                     }
                     _docBufferIndex++;
                 }
             }
        }

        // Exponential Search (Galloping)
        // Start from current block + 1
        int low = _currentBlockIndex + 1;
        if (low >= _numBlocks)
        {
            _docId = PostingsEnumConstants.NO_MORE_DOCS;
            return PostingsEnumConstants.NO_MORE_DOCS;
        }

        int high = low + 1;
        while (high < _numBlocks && _maxDocs[high] < adjustedTarget)
        {
            int newLow = high;
            high += (high - low) * 2;
            low = newLow;
        }
        
        if (high >= _numBlocks) high = _numBlocks - 1;

        // Binary Search in [low, high]
        int blockIdx = Array.BinarySearch(_maxDocs, low, high - low + 1, adjustedTarget);
        if (blockIdx < 0) blockIdx = ~blockIdx;
        
        if (blockIdx >= _numBlocks)
        {
            _docId = PostingsEnumConstants.NO_MORE_DOCS;
            return PostingsEnumConstants.NO_MORE_DOCS;
        }
        
        // Block Intersection Optimization (Skip check)
        int minDoc = _minDocs[blockIdx];
        if (adjustedTarget < minDoc)
        {
            _currentBlockIndex = blockIdx;
            _docId = minDoc;
            _docBufferIndex = 0; 
            _blockLoaded = false; // Defer loading
            return minDoc + _baseDocId;
        }
        
        // Otherwise target is inside [MinDoc, MaxDoc]. Load block and scan.
        LoadBlock(blockIdx);
        
        while (_docBufferIndex < _docBufferCount)
        {
             if (_docBuffer[_docBufferIndex] >= adjustedTarget)
             {
                 _docId = _docBuffer[_docBufferIndex];
                 _freq = _weightBuffer[_docBufferIndex];
                 _docBufferIndex++;
                 return _docId + _baseDocId;
             }
             _docBufferIndex++;
        }
        
        _docId = PostingsEnumConstants.NO_MORE_DOCS;
        return PostingsEnumConstants.NO_MORE_DOCS;
    }

    public long Cost() => _totalCount;

    private bool LoadNextBlock()
    {
        int nextIdx = _currentBlockIndex + 1;
        if (nextIdx >= _numBlocks) return false;
        LoadBlock(nextIdx);
        return true;
    }

    private void LoadBlock(int blockIndex)
    {
        _currentBlockIndex = blockIndex;
        long offset = _blockOffsets[blockIndex];
        
        byte* p = _basePtr + offset;
        
        int docBytesLen = *(int*)p; p += 4;
        
        byte* docBytesPtr = p;
        p += docBytesLen;
        
        int itemsInBlock = BlockPostingsWriter.BlockSize;
        if (blockIndex == _numBlocks - 1)
        {
            int rem = _totalCount % BlockPostingsWriter.BlockSize;
            if (rem > 0) itemsInBlock = rem;
        }
        
        fixed (byte* dest = _weightBuffer)
        {
            Buffer.MemoryCopy(p, dest, _weightBuffer.Length, itemsInBlock);
        }
        
        _docBufferCount = itemsInBlock;
        _docBufferIndex = 0;
        
        // Decompress DocIDs using GroupVarInt
        int bytesRead;
        GroupVarInt.DecodeBlock(docBytesPtr, _docBuffer, itemsInBlock, out bytesRead);
        
        // Apply Deltas
        int prev = 0;
        for(int i=0; i<itemsInBlock; i++)
        {
            _docBuffer[i] += prev;
            prev = _docBuffer[i];
        }
        
        _blockLoaded = true;
    }
}
