using System.Text;
using System.IO.MemoryMappedFiles;
using Infidex.Indexing.Compression;
using Infidex.Indexing.Fst;

namespace Infidex.Indexing.Segments;

internal unsafe class SegmentReader : IDisposable
{
    private const uint SegmentMagic = 0x494E4653; // "INFS"
    private const int SegmentVersion = 1;

    private readonly FileStream _stream;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _ptr;

    // Indexes
    private FstIndex? _fstIndex;
    private EliasFano? _offsets;

    // Metadata
    private long _postingsStart;
    private long _fstStart;
    private long _offsetsStart;
    private int _termCount;
    private int _docCount;

    public string FilePath { get; }
    public int DocCount => _docCount;
    public FstIndex? FstIndex => _fstIndex;

    public SegmentReader(string filePath)
    {
        FilePath = filePath;
        // Keep FileStream open for MMF?
        // MMF.CreateFromFile works with path or stream.
        // Using path allows FileStream to be managed by MMF or separate?
        // We need to read Header/Footer too.
        
        _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        
        // Map the whole file
        // Ensure non-empty file
        if (_stream.Length == 0)
            throw new InvalidDataException("Empty segment file");
            
        _mmf = MemoryMappedFile.CreateFromFile(_stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        
        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _ptr = ptr;

        LoadSegment();
    }

    private void LoadSegment()
    {
        // 1. Read Header from Pointer
        byte* p = _ptr;
        
        uint magic = *(uint*)p; p += 4;
        if (magic != SegmentMagic)
            throw new InvalidDataException("Invalid segment magic");
        
        int version = *(int*)p; p += 4;
        if (version != SegmentVersion)
            throw new InvalidDataException("Unsupported segment version");

        _termCount = *(int*)p; p += 4;
        _docCount = *(int*)p; p += 4;

        // 2. Read Footer
        // Footer is at end - 24 bytes
        long len = _stream.Length;
        p = _ptr + len - 24;
        
        _postingsStart = *(long*)p; p += 8;
        _fstStart = *(long*)p; p += 8;
        _offsetsStart = *(long*)p; p += 8;

        // 3. Load FST
        // FstSerializer expects BinaryReader?
        // We can create UnmanagedMemoryStream for FST loading.
        using (var ums = new UnmanagedMemoryStream(_ptr + _fstStart, _offsetsStart - _fstStart)) // Approximate length?
        using (var reader = new BinaryReader(ums, Encoding.UTF8))
        {
            _fstIndex = FstSerializer.Read(reader);
        }

        // 4. Load Offsets
        if (_termCount > 0)
        {
            // EliasFano.Read expects BinaryReader.
            // Calculate length: End of file - offsetsStart - Footer (24)?
            long offsetsLen = len - 24 - _offsetsStart;
            using (var ums = new UnmanagedMemoryStream(_ptr + _offsetsStart, offsetsLen))
            using (var reader = new BinaryReader(ums))
            {
                _offsets = EliasFano.Read(reader);
            }
        }
    }

    public IPostingsEnum? GetPostingsEnum(string term, int baseDocId = 0)
    {
        if (_fstIndex == null) return null;

        int ordinal = _fstIndex.GetExact(term.AsSpan());
        if (ordinal < 0) return null;

        return GetPostingsEnumByOrdinal(ordinal, baseDocId);
    }

    public IPostingsEnum? GetPostingsEnumByOrdinal(int ordinal, int baseDocId = 0)
    {
        if (_offsets == null || ordinal >= _offsets.Count)
            return null;

        long offset = _offsets.Get(ordinal);
        
        // Return MMap Enum struct (boxed as interface)
        return new MMapBlockPostingsEnum(_ptr, offset, baseDocId);
    }
    
    // Legacy method reimplemented efficiently
    public (int[] DocIds, byte[] Weights)? GetPostings(string term)
    {
        var en = GetPostingsEnum(term);
        if (en == null) return null;
        
        // Read all
        // Estimate capacity from Cost?
        int count = (int)en.Cost();
        int[] ids = new int[count];
        byte[] weights = new byte[count];
        
        int i = 0;
        while(en.NextDoc() != PostingsEnumConstants.NO_MORE_DOCS)
        {
            if (i < count)
            {
                ids[i] = en.DocID;
                weights[i] = (byte)en.Freq;
                i++;
            }
        }
        
        return (ids, weights);
    }

    public IEnumerable<string> GetAllTerms()
    {
        if (_fstIndex == null) return Enumerable.Empty<string>();
        return _fstIndex.EnumerateTerms();
    }

    public void Dispose()
    {
        if (_accessor != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
        }
        _mmf?.Dispose();
        _stream?.Dispose();
    }
}
