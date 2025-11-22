namespace Infidex.Benchmark;

public static class FindDocId
{
    public static void Run()
    {
        var movies = MovieData.LoadMovies();
        
        string[] searchTitles = {
            "The Shawshank Redemption",
            "Redemption",
            "Redemption Day"
        };
        
        foreach (var title in searchTitles)
        {
            var found = movies
                .Select((movie, index) => new { movie, index })
                .Where(x => x.movie.Title == title)
                .ToList();
            
            if (found.Any())
            {
                foreach (var item in found)
                {
                    Console.WriteLine($"ID {item.index}: \"{item.movie.Title}\"");
                }
            }
            else
            {
                Console.WriteLine($"NOT FOUND: \"{title}\"");
            }
        }
    }
}

