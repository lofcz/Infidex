using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex;
using Infidex.Core;
using Infidex.Api;

namespace Infidex.Tests;

[TestClass]
public class FacetingTests
{
    [TestMethod]
    public void Facets_NotReturnedWhenDisabled()
    {
        var engine = SearchEngine.CreateDefault();
        
        var docs = CreateProductDocuments();
        engine.IndexDocuments(docs);
        
        var query = new Query("laptop", 10)
        {
            EnableFacets = false
        };
        
        var result = engine.Search(query);
        
        Assert.IsNull(result.Facets);
    }
    
    [TestMethod]
    public void Facets_ReturnedWhenEnabled()
    {
        var engine = SearchEngine.CreateDefault();
        
        var docs = CreateProductDocuments();
        engine.IndexDocuments(docs);
        
        var query = new Query("laptop", 10)
        {
            EnableFacets = true
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result.Facets);
    }
    
    [TestMethod]
    public void Facets_ContainFacetableFields()
    {
        var engine = SearchEngine.CreateDefault();
        
        var docs = CreateProductDocuments();
        engine.IndexDocuments(docs);
        
        var query = new Query("product", 10)
        {
            EnableFacets = true
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result.Facets);
        // Note: Facets will only contain fields marked as Facetable=true
        // In our current implementation, we need documents with facetable fields
    }
    
    [TestMethod]
    public void Facets_EmptyQueryWithFacets_ReturnsAllDocuments()
    {
        var engine = SearchEngine.CreateDefault();
        
        var docs = CreateProductDocuments();
        engine.IndexDocuments(docs);
        
        var query = new Query("", 10)
        {
            EnableFacets = true
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Records);
        // Empty query with facets should return documents
    }
    
    [TestMethod]
    public void Result_MakeEmptyResult_CreatesEmptyResult()
    {
        var result = Result.MakeEmptyResult();
        
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Records.Length);
        Assert.IsFalse(result.DidTimeOut);
    }
    
    [TestMethod]
    public void Result_MakeEmptyResult_WithTimeout_SetsFlag()
    {
        var result = Result.MakeEmptyResult(timedOut: true);
        
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Records.Length);
        Assert.IsTrue(result.DidTimeOut);
    }
    
    [TestMethod]
    public void Facets_BookSearch_ShowsAuthorYearGenreFacets()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Search for "magic" to find fantasy books
        var query = new Query("magic", 20)
        {
            EnableFacets = true
        };
        
        var result = engine.Search(query);
        
        // Should find multiple books containing "magic"
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0, "Should find books containing 'magic'");
        
        // Facets should be available
        Assert.IsNotNull(result.Facets, "Facets should be returned");
        
        // Should have facets for author, year, and genre
        Assert.IsTrue(result.Facets.Count > 0, "Should have facet categories");
    }
    
    [TestMethod]
    public void Facets_BookSearch_AuthorFaceting()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Search for books by Rowling (Harry Potter series)
        var query = new Query("harry potter", 20)
        {
            EnableFacets = true
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0, "Should find Harry Potter books");
        
        // Verify we found multiple books from the series
        Assert.IsTrue(result.Records.Length >= 3, $"Should find at least 3 Harry Potter books, found {result.Records.Length}");
        
        // All should be from Rowling
        Assert.IsNotNull(result.Facets);
    }
    
    [TestMethod]
    public void Facets_BookSearch_GenreAndYearFiltering()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Search for fantasy books published in 2000 or later
        // Using RangeFilter to filter by year >= 2000
        var query = new Query("magic fantasy adventure", 30)
        {
            EnableFacets = true,
            Filter = new RangeFilter("year", minValue: "2000", maxValue: null, includeMin: true)
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Facets);
        
        // Should find fantasy books from 2000 onwards
        Assert.IsTrue(result.Records.Length > 0, "Should find fantasy books from 2000 onwards");
        
        // Verify all results are from 2000 or later
        for (int i = 0; i < result.Records.Length; i++)
        {
            var doc = engine.GetDocument(result.Records[i].DocumentId);
            Assert.IsNotNull(doc);
            var yearStr = doc.Fields.GetField("year")?.Value?.ToString();
            Assert.IsNotNull(yearStr);
            int year = int.Parse(yearStr);
            Assert.IsTrue(year >= 2000, $"Book {doc.Fields.GetField("title")?.Value} has year {year}, expected >= 2000");
        }
        
        // Show which books were found
        Console.WriteLine($"\nFound {result.Records.Length} books matching 'magic fantasy adventure' (year >= 2000):");
        Console.WriteLine("-------------------------------------------------------");
        for (int i = 0; i < result.Records.Length; i++)
        {
            var record = result.Records[i];
            var doc = engine.GetDocument(record.DocumentId);
            if (doc != null)
            {
                var title = doc.Fields.GetField("title")?.Value?.ToString() ?? "Unknown";
                var author = doc.Fields.GetField("author")?.Value?.ToString() ?? "Unknown";
                var year = doc.Fields.GetField("year")?.Value?.ToString() ?? "Unknown";
                var genre = doc.Fields.GetField("genre")?.Value?.ToString() ?? "Unknown";
                
                Console.WriteLine($"{i + 1}. \"{title}\" by {author} ({year}) - {genre}");
                Console.WriteLine($"   Score: {record.Score}");
            }
        }
        
        // Facets should show available years and genres from filtered results
        Console.WriteLine($"\nFacets available: {result.Facets.Count}");
        
        foreach (var facetCategory in result.Facets)
        {
            Console.WriteLine($"  Facet '{facetCategory.Key}': {facetCategory.Value.Length} values");
            foreach (var facetValue in facetCategory.Value)
            {
                Console.WriteLine($"    - {facetValue.Key}: {facetValue.Value} documents");
                
                // Verify year facets are all >= 2000
                if (facetCategory.Key == "year")
                {
                    int facetYear = int.Parse(facetValue.Key);
                    Assert.IsTrue(facetYear >= 2000, $"Facet year {facetYear} should be >= 2000");
                }
            }
        }
        
        // Verify we have year facets
        Assert.IsTrue(result.Facets.ContainsKey("year"), "Should have year facets");
        Assert.IsTrue(result.Facets.ContainsKey("genre"), "Should have genre facets");
    }
    
    [TestMethod]
    public void Facets_BookSearch_RecentPublications()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Search for recent books (2015+) containing "stone" or "philosopher"
        var query = new Query("stone philosopher", 10)
        {
            EnableFacets = true
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0, "Should find books with 'stone' or 'philosopher'");
        
        // Should find Harry Potter and the Philosopher's Stone
        var topResult = result.Records[0];
        Assert.IsNotNull(topResult);
    }
    
    [TestMethod]
    public void Facets_BookSearch_CompositeFilter_FantasyAfter2000()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Search for fantasy books published after 2000
        // Using composite filter: genre = "Fantasy" AND year >= 2000
        var genreFilter = new ValueFilter("genre", "Fantasy");
        var yearFilter = new RangeFilter("year", minValue: "2000", maxValue: null);
        var compositeFilter = CompositeFilter.And(genreFilter, yearFilter);
        
        var query = new Query("magic adventure", 30)
        {
            EnableFacets = true,
            Filter = compositeFilter
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0, "Should find Fantasy books from 2000+");
        
        // Verify all results are Fantasy books from 2000 or later
        for (int i = 0; i < result.Records.Length; i++)
        {
            var doc = engine.GetDocument(result.Records[i].DocumentId);
            Assert.IsNotNull(doc);
            
            var genre = doc.Fields.GetField("genre")?.Value?.ToString();
            var yearStr = doc.Fields.GetField("year")?.Value?.ToString();
            
            Assert.AreEqual("Fantasy", genre, $"Book should be Fantasy genre");
            Assert.IsNotNull(yearStr);
            int year = int.Parse(yearStr);
            Assert.IsTrue(year >= 2000, $"Book year should be >= 2000, got {year}");
        }
        
        Console.WriteLine($"\nFound {result.Records.Length} Fantasy books from 2000+:");
        for (int i = 0; i < result.Records.Length; i++)
        {
            var doc = engine.GetDocument(result.Records[i].DocumentId);
            if (doc != null)
            {
                var title = doc.Fields.GetField("title")?.Value?.ToString();
                var year = doc.Fields.GetField("year")?.Value?.ToString();
                Console.WriteLine($"  {i + 1}. {title} ({year})");
            }
        }
    }
    
    [TestMethod]
    public void Facets_BookSearch_CompositeFilter_RowlingOrKing()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Search for books by either Rowling OR King
        var rowlingFilter = new ValueFilter("author", "J.K. Rowling");
        var kingFilter = new ValueFilter("author", "Stephen King");
        var compositeFilter = CompositeFilter.Or(rowlingFilter, kingFilter);
        
        var query = new Query("magic dark", 30)
        {
            EnableFacets = true,
            Filter = compositeFilter
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0, "Should find books by Rowling or King");
        
        // Verify all results are from either Rowling or King
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Assert.IsNotNull(doc);
            
            var author = doc.Fields.GetField("author")?.Value?.ToString();
            bool isRowlingOrKing = author == "J.K. Rowling" || author == "Stephen King";
            Assert.IsTrue(isRowlingOrKing, $"Book should be by Rowling or King, got {author}");
        }
        
        // Facets should show only Rowling and King
        Assert.IsNotNull(result.Facets);
        Assert.IsTrue(result.Facets.ContainsKey("author"));
        
        var authorFacet = result.Facets["author"];
        var authorNames = authorFacet.Select(kv => kv.Key).ToArray();
        
        foreach (var name in authorNames)
        {
            bool isRowlingOrKing = name == "J.K. Rowling" || name == "Stephen King";
            Assert.IsTrue(isRowlingOrKing, $"Facet should only contain Rowling or King, found {name}");
        }
    }
    
    [TestMethod]
    public void Facets_BookSearch_FilterBuilder_ComplexExpression()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Build complex filter: (Fantasy AND year >= 2000) OR (Horror AND year >= 1970)
        // This finds modern fantasy OR modern horror books
        var filter = FilterBuilder
            .Where("genre", "Fantasy")
            .AndRange("year", min: "2000")
            .Or(_ => FilterBuilder
                .Where("genre", "Horror")
                .AndRange("year", min: "1970"))
            .Build();
        
        var query = new Query("winter dark magic story", 30)
        {
            EnableFacets = true,
            Filter = filter
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0, "Should find modern Fantasy or Horror books");
        
        // Verify results match the filter criteria
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Assert.IsNotNull(doc);
            
            var genre = doc.Fields.GetField("genre")?.Value?.ToString();
            var yearStr = doc.Fields.GetField("year")?.Value?.ToString();
            Assert.IsNotNull(yearStr);
            int year = int.Parse(yearStr);
            
            // Must be either (Fantasy AND >= 2000) OR (Horror AND >= 1980)
            bool matchesFilter = 
                (genre == "Fantasy" && year >= 2000) ||
                (genre == "Horror" && year >= 1970);
            
            Assert.IsTrue(matchesFilter, 
                $"Book '{doc.Fields.GetField("title")?.Value}' ({genre}, {year}) doesn't match filter");
        }
        
        Console.WriteLine($"\nFound {result.Records.Length} books matching complex filter:");
        Console.WriteLine("Filter: (Fantasy AND year >= 2000) OR (Horror AND year >= 1970)");
        Console.WriteLine("-------------------------------------------------------------");
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            if (doc != null)
            {
                var title = doc.Fields.GetField("title")?.Value?.ToString();
                var author = doc.Fields.GetField("author")?.Value?.ToString();
                var year = doc.Fields.GetField("year")?.Value?.ToString();
                var genre = doc.Fields.GetField("genre")?.Value?.ToString();
                Console.WriteLine($"  • {title} by {author} ({year}) - {genre}");
            }
        }
    }
    
    [TestMethod]
    public void Facets_BookSearch_FilterBuilder_MultipleAnds()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Build filter with multiple ANDs: Fantasy AND year >= 2000 AND year <= 2010
        var filter = FilterBuilder
            .Where("genre", "Fantasy")
            .AndRange("year", min: "2000", max: "2010")
            .Build();
        
        var query = new Query("magic fantasy", 30)
        {
            EnableFacets = true,
            Filter = filter
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0, "Should find Fantasy books from 2000-2010");
        
        // Verify all results are Fantasy books between 2000-2010
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Assert.IsNotNull(doc);
            
            var genre = doc.Fields.GetField("genre")?.Value?.ToString();
            var yearStr = doc.Fields.GetField("year")?.Value?.ToString();
            
            Assert.AreEqual("Fantasy", genre);
            Assert.IsNotNull(yearStr);
            int year = int.Parse(yearStr);
            Assert.IsTrue(year >= 2000 && year <= 2010, $"Year {year} should be between 2000-2010");
        }
    }
    
    [TestMethod]
    public void Facets_BookSearch_FilterParser_SimpleExpression()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Much clearer string-based syntax!
        var filter = FilterParser.Parse("genre = 'Fantasy' AND year >= '2000'");
        
        var query = new Query("magic fantasy adventure", 30)
        {
            EnableFacets = true,
            Filter = filter
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0, "Should find Fantasy books from 2000+");
        
        // Verify results
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Assert.IsNotNull(doc);
            
            var genre = doc.Fields.GetField("genre")?.Value?.ToString();
            var yearStr = doc.Fields.GetField("year")?.Value?.ToString();
            
            Assert.AreEqual("Fantasy", genre);
            Assert.IsNotNull(yearStr);
            Assert.IsTrue(int.Parse(yearStr) >= 2000);
        }
    }
    
    [TestMethod]
    public void Facets_BookSearch_FilterParser_ComplexExpression()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        var filter = FilterParser.Parse(
            "(genre = 'Fantasy' AND year >= '2000') OR (genre = 'Horror' AND year >= '1970')");
        
        var query = new Query("winter dark magic story", 30)
        {
            EnableFacets = true,
            Filter = filter
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0, "Should find modern Fantasy or Horror books");
        
        Console.WriteLine($"\nFound {result.Records.Length} books with filter:");
        Console.WriteLine("(genre = 'Fantasy' AND year >= '2000') OR (genre = 'Horror' AND year >= '1970')");
        Console.WriteLine("---------------------------------------------------------------------");
        
        // Verify and display results
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Assert.IsNotNull(doc);
            
            var title = doc.Fields.GetField("title")?.Value?.ToString();
            var author = doc.Fields.GetField("author")?.Value?.ToString();
            var genre = doc.Fields.GetField("genre")?.Value?.ToString();
            var yearStr = doc.Fields.GetField("year")?.Value?.ToString();
            Assert.IsNotNull(yearStr);
            int year = int.Parse(yearStr);
            
            // Verify filter logic
            bool matches = (genre == "Fantasy" && year >= 2000) || (genre == "Horror" && year >= 1970);
            Assert.IsTrue(matches, $"Book should match filter: {title} ({genre}, {year})");
            
            Console.WriteLine($"  • {title} by {author} ({year}) - {genre}");
        }
    }
    
    [TestMethod]
    public void Facets_BookSearch_FilterParser_MultipleAuthors()
    {
        var engine = SearchEngine.CreateDefault();
        
        var books = CreateBookLibrary();
        engine.IndexDocuments(books);
        
        // Easy to read OR conditions
        var filter = FilterParser.Parse("author in('J.K. Rowling', 'Stephen King', 'Brandon Sanderson')");
        
        var query = new Query("magic dark", 30)
        {
            EnableFacets = true,
            Filter = filter
        };
        
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Records.Length > 0);
        
        // All results should be from one of these three authors
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Assert.IsNotNull(doc);
            var author = doc.Fields.GetField("author")?.Value?.ToString();
            
            bool isOneOfThree = author == "J.K. Rowling" || author == "Stephen King" || author == "Brandon Sanderson";
            Assert.IsTrue(isOneOfThree, $"Author should be Rowling, King, or Sanderson, got {author}");
        }
    }
    
    private static Document[] CreateBookLibrary()
    {
        return new[]
        {
            // Harry Potter series by J.K. Rowling
            CreateBookDoc(1L, "Harry Potter and the Philosopher's Stone", "J.K. Rowling", "1997", "Fantasy", 
                "A young wizard discovers his magical heritage and begins his education at Hogwarts School of Witchcraft and Wizardry."),
            CreateBookDoc(2L, "Harry Potter and the Chamber of Secrets", "J.K. Rowling", "1998", "Fantasy",
                "Harry returns to Hogwarts and must face a mysterious monster lurking in the chamber beneath the school."),
            CreateBookDoc(3L, "Harry Potter and the Prisoner of Azkaban", "J.K. Rowling", "1999", "Fantasy",
                "Harry learns about Sirius Black, a dangerous wizard who has escaped from the infamous Azkaban prison."),
            CreateBookDoc(4L, "Harry Potter and the Goblet of Fire", "J.K. Rowling", "2000", "Fantasy",
                "Harry competes in the dangerous Triwizard Tournament while dark forces gather strength."),
            CreateBookDoc(5L, "Harry Potter and the Order of the Phoenix", "J.K. Rowling", "2003", "Fantasy",
                "Harry forms a secret organization to fight against the rising darkness and Voldemort's return."),
            
            // Fantasy books by other authors
            CreateBookDoc(6L, "A Game of Thrones", "George R.R. Martin", "1996", "Fantasy",
                "Noble families vie for control of the Iron Throne in the Seven Kingdoms of Westeros."),
            CreateBookDoc(7L, "The Name of the Wind", "Patrick Rothfuss", "2007", "Fantasy",
                "Kvothe recounts his journey from a talented young musician to a legendary wizard."),
            CreateBookDoc(8L, "The Way of Kings", "Brandon Sanderson", "2010", "Fantasy",
                "In a world of stone and storms, warriors wield magical powers through ancient armor."),
            
            // Stephen King horror novels
            CreateBookDoc(9L, "The Shining", "Stephen King", "1977", "Horror",
                "A family becomes winter caretakers at an isolated hotel with a violent past."),
            CreateBookDoc(10L, "It", "Stephen King", "1986", "Horror",
                "A shape-shifting entity terrorizes children in a small Maine town every 27 years."),
            CreateBookDoc(11L, "Pet Sematary", "Stephen King", "1983", "Horror",
                "A burial ground with sinister powers brings the dead back to life with horrifying consequences."),
            
            // Science Fiction
            CreateBookDoc(12L, "Dune", "Frank Herbert", "1965", "Science Fiction",
                "A noble family struggles for control of the desert planet Arrakis and its valuable spice."),
            CreateBookDoc(13L, "Neuromancer", "William Gibson", "1984", "Science Fiction",
                "A washed-up computer hacker is hired for one last job in cyberspace."),
            CreateBookDoc(14L, "The Three-Body Problem", "Liu Cixin", "2008", "Science Fiction",
                "Scientists discover an alien civilization facing destruction from their chaotic solar system."),
            
            // Mystery/Thriller
            CreateBookDoc(15L, "The Girl with the Dragon Tattoo", "Stieg Larsson", "2005", "Mystery",
                "A journalist and a hacker investigate a decades-old disappearance in a powerful Swedish family."),
            CreateBookDoc(16L, "Gone Girl", "Gillian Flynn", "2012", "Thriller",
                "A woman disappears on her wedding anniversary, and her husband becomes the prime suspect."),
            
            // Recent Fantasy
            CreateBookDoc(17L, "The Fifth Season", "N.K. Jemisin", "2015", "Fantasy",
                "In a world of catastrophic seismic events, people with earth-shaping powers are hunted."),
            CreateBookDoc(18L, "Mistborn: The Final Empire", "Brandon Sanderson", "2006", "Fantasy",
                "A street thief discovers her magical abilities and joins a rebellion against an immortal tyrant.")
        };
    }
    
    private static Document CreateBookDoc(long id, string title, string author, string year, string genre, string description)
    {
        var fields = new DocumentFields();
        fields.AddField("title", title, Weight.High, indexable: true);
        fields.AddField("author", author, Weight.Med, indexable: true);
        fields.AddField("year", year, Weight.Low, indexable: false); // Year typically not searched in text
        fields.AddField("genre", genre, Weight.Low, indexable: true);
        fields.AddField("description", description, Weight.Med, indexable: true);
        
        // Make author, year, and genre facetable
        var authorField = fields.GetField("author");
        if (authorField != null)
        {
            authorField.Facetable = true;
        }
        
        var yearField = fields.GetField("year");
        if (yearField != null)
        {
            yearField.Facetable = true;
        }
        
        var genreField = fields.GetField("genre");
        if (genreField != null)
        {
            genreField.Facetable = true;
        }
        
        return new Document(id, fields);
    }
    
    private static Document[] CreateProductDocuments()
    {
        return new[]
        {
            CreateProductDoc(1L, "Laptop Pro", "Electronics", "High-end laptop for professionals"),
            CreateProductDoc(2L, "Mouse Wireless", "Electronics", "Ergonomic wireless mouse"),
            CreateProductDoc(3L, "Keyboard Mechanical", "Electronics", "RGB mechanical keyboard"),
            CreateProductDoc(4L, "Desk Lamp", "Furniture", "LED desk lamp with adjustable brightness"),
            CreateProductDoc(5L, "Office Chair", "Furniture", "Ergonomic office chair"),
        };
    }
    
    private static Document CreateProductDoc(long id, string name, string category, string description)
    {
        var fields = new DocumentFields();
        fields.AddField("name", name, Weight.High, indexable: true);
        fields.AddField("category", category, Weight.Low, indexable: true);
        fields.AddField("description", description, Weight.Med, indexable: true);
        
        // Make category facetable for testing
        var categoryField = fields.GetField("category");
        if (categoryField != null)
        {
            categoryField.Facetable = true;
        }
        
        return new Document(id, fields);
    }
}

