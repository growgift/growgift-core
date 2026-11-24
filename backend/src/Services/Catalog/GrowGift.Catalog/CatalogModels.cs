namespace GrowGift.Catalog;

/// <summary>
/// Curated, kid-friendly collections. Symbols are reviewed by compliance
/// before publication (general-advice warnings apply under ASIC RG 244);
/// the catalog never surfaces performance projections.
/// </summary>
public sealed class Collection
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = default!;
    public string Title { get; init; } = default!;
    public string Tagline { get; init; } = default!;
    public string Emoji { get; init; } = default!;
    public int SortOrder { get; init; }
    public List<CollectionEntry> Entries { get; init; } = [];
}

public sealed class CollectionEntry
{
    public Guid Id { get; init; }
    public Guid CollectionId { get; init; }
    public string Symbol { get; init; } = default!;
    public string CompanyName { get; init; } = default!;
    public string KidFriendlyBlurb { get; init; } = default!;
}

public static class CatalogSeed
{
    public static List<Collection> Collections() =>
    [
        New("gaming", "Gaming", "Companies behind the games they already love", "🎮", 1,
            ("MSFT", "Microsoft", "Makes Xbox and Minecraft"),
            ("NTDOY", "Nintendo", "Home of Mario and Zelda"),
            ("RBLX", "Roblox", "The platform where kids build their own games"),
            ("EA", "Electronic Arts", "FIFA, Madden and The Sims")),
        New("space", "Space & Beyond", "For the kid who stares at the stars", "🚀", 2,
            ("RKLB", "Rocket Lab", "Launches small rockets to orbit"),
            ("BA", "Boeing", "Builds planes and spacecraft"),
            ("LMT", "Lockheed Martin", "Built parts of the Mars rovers")),
        New("ai-robotics", "AI & Robotics", "The technology shaping their future", "🤖", 3,
            ("NVDA", "NVIDIA", "Makes the chips that power AI"),
            ("GOOGL", "Alphabet", "Google's parent, an AI pioneer"),
            ("ISRG", "Intuitive Surgical", "Robots that help doctors in surgery")),
        New("everyday", "Brands They Know", "Companies kids see every day", "🛒", 4,
            ("DIS", "Disney", "Movies, parks and Disney+"),
            ("MCD", "McDonald's", "The golden arches"),
            ("AAPL", "Apple", "iPhones and iPads"),
            ("KO", "Coca-Cola", "The world's most famous drink")),
        New("planet", "Planet Friendly", "Companies working on a cleaner world", "🌍", 5,
            ("TSLA", "Tesla", "Electric cars and batteries"),
            ("ENPH", "Enphase", "Solar power technology"),
            ("WM", "Waste Management", "Recycling at scale")),
        New("automobiles", "Automobiles", "The companies behind the cars they spot on the road", "🚗", 6,
            ("TSLA", "Tesla", "Electric cars and self-driving tech"),
            ("TM", "Toyota", "One of the world's biggest carmakers"),
            ("F", "Ford", "The company that put the world on wheels"),
            ("RACE", "Ferrari", "The legendary red racing cars")),
        New("sports", "Sports & Fitness", "For the kid who lives on the field", "⚽", 7,
            ("NKE", "Nike", "The swoosh on their sneakers"),
            ("ADDYY", "Adidas", "Three stripes, worn by champions"),
            ("MANU", "Manchester United", "Own a piece of a football club")),
        New("food", "Food & Treats", "Brands from their favourite snacks", "🍫", 8,
            ("HSY", "Hershey", "Chocolate, Kisses and Reese's"),
            ("MDLZ", "Mondelez", "Oreo, Cadbury and Toblerone"),
            ("DPZ", "Domino's", "Pizza night, delivered"))
    ];

    private static Collection New(string slug, string title, string tagline, string emoji, int sort,
        params (string Symbol, string Name, string Blurb)[] entries)
    {
        var id = Guid.NewGuid();
        return new Collection
        {
            Id = id, Slug = slug, Title = title, Tagline = tagline, Emoji = emoji, SortOrder = sort,
            Entries = entries.Select(e => new CollectionEntry
            {
                Id = Guid.NewGuid(), CollectionId = id,
                Symbol = e.Symbol, CompanyName = e.Name, KidFriendlyBlurb = e.Blurb
            }).ToList()
        };
    }
}
