using Infidex.Tokenization;

namespace Infidex.Core;

/// <summary>
/// Configuration parameters for the search engine.
/// </summary>
public class ConfigurationParameters
{
    private static readonly Dictionary<int, ConfigurationParameters> PredefinedConfigs = [];
    
    /// <summary>
    /// Default field weights for High, Med, Low field importance.
    /// Index 0 = High (1.5x), Index 1 = Med (1.25x), Index 2 = Low (1.0x)
    /// </summary>
    public static readonly float[] DefaultFieldWeights = [1.5f, 1.25f, 1.0f];
    
    public int[] IndexSizes { get; set; }
    public int StartPadSize { get; set; }
    public int StopPadSize { get; set; }
    public int StopTermLimit { get; set; }
    public bool CaseSensitive { get; set; }
    public int MaxIndexTextLength { get; set; }
    public int MaxClientTextLength { get; set; }
    public int MaxDocuments { get; set; }
    public TextNormalizer? TextNormalizer { get; set; }
    public TokenizerSetup? TokenizerSetup { get; set; }
    public bool DeleteTextAfterIndexing { get; set; }
    public AutoSegmentationSetup? AutoSegmentationSetup { get; set; }
    public int FilterCacheSize { get; set; }
    public float[] FieldWeights { get; set; }
    public WordMatcherSetup? WordMatcherSetup { get; set; }
    
    public ConfigurationParameters()
    {
        IndexSizes = [2, 3];
        StartPadSize = 2;
        StopPadSize = 0;
        StopTermLimit = 1_250_000;
        CaseSensitive = false;
        MaxIndexTextLength = 300;
        MaxClientTextLength = 1000;
        MaxDocuments = 5_000_000;
        DeleteTextAfterIndexing = false;
        FilterCacheSize = 0;
        FieldWeights = DefaultFieldWeights;
    }
    
    static ConfigurationParameters()
    {
        SetupPredefinedConfigs();
    }
    
    private static void SetupPredefinedConfigs()
    {
        Dictionary<char, char> charReplacements = new Dictionary<char, char>
        {
            { 'Æ', 'E' }, { 'æ', 'e' },
            { 'Ø', 'O' }, { 'ø', 'o' },
            { 'Å', 'A' }, { 'å', 'a' },
            { 'Ä', 'A' }, { 'ä', 'a' },
            { 'Ö', 'O' }, { 'ö', 'o' },
            { 'Ü', 'U' }, { 'ü', 'u' },
            { 'ß', 's' }
        };
        
        Dictionary<string, string> stringReplacements = new Dictionary<string, string>
        {
            { "  ", " " },
            { "\t", " " },
            { "\n", " " },
            { "\r", " " }
        };
        
        TextNormalizer textNormalizer = new TextNormalizer(stringReplacements, charReplacements, oneWayMode: true);
        
        char[] delimiters =
        [
            ' ', '-', '/', '.', ',', ':', ';', '\'', '`', '–', '—',
            '*', '&', '\\', '_', '(', ')', '{', '}', '[', ']', '\t'
        ];
        
        // Config 100: Default configuration (dual n-grams, no word matcher)
        PredefinedConfigs[100] = new ConfigurationParameters
        {
            IndexSizes = [2, 3],
            StartPadSize = 2,
            StopPadSize = 0,
            StopTermLimit = 1_250_000,
            CaseSensitive = false,
            MaxIndexTextLength = 300,
            MaxClientTextLength = 1000,
            MaxDocuments = 5_000_000,
            TextNormalizer = textNormalizer,
            TokenizerSetup = new TokenizerSetup(delimiters, highResolutionMode: false, removeDuplicateTokens: true),
            DeleteTextAfterIndexing = false,
            FilterCacheSize = 0,
            FieldWeights = DefaultFieldWeights
        };
        
        // Config 103: Single n-gram, similar to 100
        PredefinedConfigs[103] = new ConfigurationParameters
        {
            IndexSizes = [3],
            StartPadSize = 2,
            StopPadSize = 0,
            StopTermLimit = 1_250_000,
            CaseSensitive = false,
            MaxIndexTextLength = 300,
            MaxClientTextLength = 1000,
            MaxDocuments = 5_000_000,
            TextNormalizer = textNormalizer,
            TokenizerSetup = new TokenizerSetup(delimiters, highResolutionMode: false, removeDuplicateTokens: true),
            DeleteTextAfterIndexing = false,
            FilterCacheSize = 0,
            FieldWeights = DefaultFieldWeights
        };
        
        // Config 400: Advanced with WordMatcher and auto-segmentation
        PredefinedConfigs[400] = new ConfigurationParameters
        {
            IndexSizes = [3],
            StartPadSize = 2,
            StopPadSize = 0,
            StopTermLimit = 1_250_000,
            CaseSensitive = false,
            MaxIndexTextLength = 300,
            MaxClientTextLength = 1000,
            MaxDocuments = 5_000_000,
            TextNormalizer = textNormalizer,
            TokenizerSetup = new TokenizerSetup(delimiters, highResolutionMode: false, removeDuplicateTokens: false),
            DeleteTextAfterIndexing = true,
            AutoSegmentationSetup = new AutoSegmentationSetup(200, 0.2),
            FilterCacheSize = 200_000,
            FieldWeights = DefaultFieldWeights,
            WordMatcherSetup = new WordMatcherSetup(
                MaximumWordSizeExact: 8,
                MaximumWordSizeLD1: 8,
                MinimumWordSizeExact: 2,
                MinimumWordSizeLD1: 3,
                SupportLD1: true,
                SupportAffix: true)
        };
        
        // Config 401: Similar to 400 with different settings
        PredefinedConfigs[401] = new ConfigurationParameters
        {
            IndexSizes = [3],
            StartPadSize = 2,
            StopPadSize = 0,
            StopTermLimit = 1_250_000,
            CaseSensitive = false,
            MaxIndexTextLength = 300,
            MaxClientTextLength = 1000,
            MaxDocuments = 5_000_000,
            TextNormalizer = textNormalizer,
            TokenizerSetup = new TokenizerSetup(delimiters, highResolutionMode: false, removeDuplicateTokens: false),
            DeleteTextAfterIndexing = true,
            AutoSegmentationSetup = new AutoSegmentationSetup(200, 0.2),
            FilterCacheSize = 200_000,
            FieldWeights = DefaultFieldWeights,
            WordMatcherSetup = new WordMatcherSetup(
                MaximumWordSizeExact: 8,
                MaximumWordSizeLD1: 8,
                MinimumWordSizeExact: 2,
                MinimumWordSizeLD1: 3,
                SupportLD1: true,
                SupportAffix: true)
        };
    }
    
    /// <summary>
    /// Gets a predefined configuration by number
    /// </summary>
    public static ConfigurationParameters GetConfig(int configNumber)
    {
        if (PredefinedConfigs.TryGetValue(configNumber, out ConfigurationParameters? config))
            return config;
        
        throw new ArgumentException($"Configuration {configNumber} not found");
    }
    
    /// <summary>
    /// Checks if a configuration number exists
    /// </summary>
    public static bool HasConfig(int configNumber)
    {
        return PredefinedConfigs.ContainsKey(configNumber);
    }
}

/// <summary>
/// Configuration for automatic document segmentation
/// </summary>
public class AutoSegmentationSetup
{
    public int TargetSegmentSize { get; set; }
    public double OverlapRatio { get; set; }
    
    public AutoSegmentationSetup(int targetSegmentSize, double overlapRatio)
    {
        TargetSegmentSize = targetSegmentSize;
        OverlapRatio = overlapRatio;
    }
}

/// <summary>
/// Configuration for WordMatcher
/// </summary>
public class WordMatcherSetup
{
    public int MaximumWordSizeExact { get; set; }
    public int MaximumWordSizeLD1 { get; set; }
    public int MinimumWordSizeExact { get; set; }
    public int MinimumWordSizeLD1 { get; set; }
    public bool SupportLD1 { get; set; }
    public bool SupportAffix { get; set; }
    
    public WordMatcherSetup(
        int MaximumWordSizeExact = 8,
        int MaximumWordSizeLD1 = 8,
        int MinimumWordSizeExact = 2,
        int MinimumWordSizeLD1 = 3,
        bool SupportLD1 = false,
        bool SupportAffix = false)
    {
        this.MaximumWordSizeExact = MaximumWordSizeExact;
        this.MaximumWordSizeLD1 = MaximumWordSizeLD1;
        this.MinimumWordSizeExact = MinimumWordSizeExact;
        this.MinimumWordSizeLD1 = MinimumWordSizeLD1;
        this.SupportLD1 = SupportLD1;
        this.SupportAffix = SupportAffix;
    }
}


