using System.Reflection;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The selector that makes a seed reproducible: same token, same answer; different tokens, spread evenly; and a
/// catalogue edit that does not move picks it has no business moving.
/// </summary>
/// <remarks>
/// <c>ContentSeedSelector</c> is internal and has no injected anything, so it is reached by reflection rather than
/// through the facade. Worth it: these are the properties the whole feature's reproducibility claim rests on, and
/// asserting them through four layers of HTTP would prove them far less directly.
/// </remarks>
public sealed class ContentSeedSelectorTests
{
    private static readonly MethodInfo Selector = typeof(ReplaceWeeklyThemesViewModel).Assembly
        .GetType("CreatorPantry.Domain.Modules.Content.Managers.ContentSeedSelector", throwOnError: true)!
        .GetMethod("Select", BindingFlags.Public | BindingFlags.Static)!;

    private static string? Select(string token, string facet, IReadOnlyList<string> candidates) =>
        (string?)Selector
            .MakeGenericMethod(typeof(string))
            .Invoke(null, [token, facet, candidates, (Func<string, string>)(candidate => candidate)]);

    private static readonly string[] Cuisines =
    [
        "american", "southern-us", "tex-mex", "mexican", "caribbean", "italian", "french", "spanish", "greek",
        "mediterranean", "middle-eastern", "indian", "thai", "vietnamese", "chinese", "japanese", "korean",
    ];

    [Fact]
    public void The_same_token_always_selects_the_same_candidate()
    {
        var first = Select("spring-bakes", "cuisine", Cuisines);

        foreach (var _ in Enumerable.Range(0, 20))
        {
            Assert.Equal(first, Select("spring-bakes", "cuisine", Cuisines));
        }
    }

    [Fact]
    public void A_facet_with_no_candidates_selects_nothing()
    {
        // An empty or fully retired catalogue is a facet the seed omits, not an error.
        Assert.Null(Select("spring-bakes", "cuisine", []));
    }

    [Fact]
    public void A_single_candidate_is_always_the_answer() =>
        Assert.Equal("thai", Select("any-token", "cuisine", ["thai"]));

    [Fact]
    public void Different_facets_decide_independently_for_one_token()
    {
        // Without the facet in the hash, two facets over the same keys would always agree, and a seed would be
        // far less varied than it looks.
        var agreements = 0;
        foreach (var index in Enumerable.Range(0, 200))
        {
            var token = $"token-{index}";
            if (Select(token, "cuisine", Cuisines) == Select(token, "occasion", Cuisines))
            {
                agreements++;
            }
        }

        // Independent facets agree about 1/17 of the time. Anything near 200 would mean the facet name is not
        // reaching the hash.
        Assert.InRange(agreements, 0, 40);
    }

    [Fact]
    public void Candidate_order_does_not_change_the_answer()
    {
        // A read with no ORDER BY would otherwise make the same token answer differently run to run.
        var forwards = Select("spring-bakes", "cuisine", Cuisines);
        var backwards = Select("spring-bakes", "cuisine", [.. Cuisines.Reverse()]);
        var shuffled = Select("spring-bakes", "cuisine", [.. Cuisines.OrderBy(code => code.Length).ThenBy(code => code)]);

        Assert.Equal(forwards, backwards);
        Assert.Equal(forwards, shuffled);
    }

    [Fact]
    public void Adding_a_candidate_leaves_almost_every_token_alone()
    {
        // The reason selection scores each candidate instead of indexing into a sorted list: a shared seed link
        // has to keep meaning what it meant after the catalogue grows.
        var extended = new List<string>(Cuisines) { "peruvian" };

        var moved = Enumerable.Range(0, 400)
            .Count(index => Select($"token-{index}", "cuisine", Cuisines)
                != Select($"token-{index}", "cuisine", extended));

        // One in eighteen tokens should now land on the new entry, and no others should move at all.
        Assert.InRange(moved, 1, 60);
    }

    [Fact]
    public void Removing_the_selected_candidate_is_the_case_that_does_move()
    {
        // Stated as a test because it is the documented limit of reproducibility, not a bug.
        var chosen = Select("spring-bakes", "cuisine", Cuisines)!;
        var without = Cuisines.Where(code => code != chosen).ToList();

        var next = Select("spring-bakes", "cuisine", without);

        Assert.NotNull(next);
        Assert.NotEqual(chosen, next);
    }

    [Fact]
    public void Selection_is_spread_across_the_catalogue()
    {
        // Uniform by construction, so a thousand tokens should touch every entry rather than favouring a few.
        var counts = Enumerable.Range(0, 1000)
            .Select(index => Select($"token-{index}", "cuisine", Cuisines)!)
            .GroupBy(code => code)
            .ToDictionary(group => group.Key, group => group.Count());

        Assert.Equal(Cuisines.Length, counts.Count);

        // Expected is 1000/17 ≈ 59. A generous band: this asserts nothing is starved or dominant, not that the
        // hash is a statistics textbook.
        Assert.All(counts.Values, count => Assert.InRange(count, 15, 140));
    }

    [Fact]
    public void A_token_is_taken_literally()
    {
        // Tokens that differ only in case or by one character are different seeds. Worth pinning: a creator who
        // types their own token gets what they typed.
        Assert.NotEqual(Select("Spring-Bakes", "cuisine", Cuisines), Select("spring-bakes", "cuisine", Cuisines));
        Assert.NotEqual(Select("spring-bakes", "cuisine", Cuisines), Select("spring-bake", "cuisine", Cuisines));
    }
}
