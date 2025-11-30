using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;

namespace Infidex.Benchmark;

public class MovieRecord
{
    [Name("title")]
    public string Title { get; set; } = string.Empty;
    
    [Name("description")]
    public string Description { get; set; } = string.Empty;
    
    [Name("genre")]
    public string Genre { get; set; } = string.Empty;
    
    [Name("year")]
    public string Year { get; set; } = string.Empty;
}

public static class MovieData
{
    private static List<MovieRecord>? _cachedMovies;
    
    public static List<MovieRecord> LoadMovies()
    {
        if (_cachedMovies != null)
        {
            return _cachedMovies;
        }
        
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.ToLower(),
            MissingFieldFound = null, // Ignore missing fields
            HeaderValidated = null // Ignore missing headers
        };

        string filePath = Path.Combine(AppContext.BaseDirectory, "movies1M.csv");
        
        // Try to find 1M dataset
        string[] possiblePaths = 
        {
            Path.Combine(AppContext.BaseDirectory, "../../../../src/Infidex.Tests/movies1M.csv"),
            Path.Combine(AppContext.BaseDirectory, "../../../../src/Infidex.Example/movies.csv"),
            filePath
        };
        
        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                filePath = path;
                Console.WriteLine($"Loading movies from: {filePath}");
                break;
            }
        }
        
        using (var reader = new StreamReader(filePath))
        using (var csv = new CsvReader(reader, config))
        {
            _cachedMovies = csv.GetRecords<MovieRecord>().ToList();
        }

        return _cachedMovies;
    }
}
