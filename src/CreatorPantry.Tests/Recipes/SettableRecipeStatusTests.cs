using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The seam between the status a request may ask for and the status the domain records.
/// </summary>
/// <remarks>
/// <para>
/// Splitting one enum into two buys an input contract that read-only workflow states cannot widen, and costs a
/// pair that can drift. These are the tests that make the drift loud: they are driven from the enum members
/// rather than from a list, so adding a state to either side without the other fails here rather than in a
/// client months later.
/// </para>
/// <para>
/// <strong>The checks are deliberately one-directional.</strong> Every settable status must be a domain state;
/// a domain state need not be settable. That asymmetry is the entire reason the two types exist, so there is no
/// test asserting the reverse — one would forbid exactly the growth this split was made to allow, and the first
/// read-only state added would have to delete it.
/// </para>
/// </remarks>
public sealed class SettableRecipeStatusTests
{
    private static readonly SettableRecipeStatusViewModel[] EverySettableStatus = Enum.GetValues<SettableRecipeStatusViewModel>();

    [Fact]
    public void Every_settable_status_maps_to_a_declared_domain_state()
    {
        foreach (var settable in EverySettableStatus)
        {
            var mapped = SettableRecipeStatus.ToDomain(settable);

            Assert.True(
                Enum.IsDefined(mapped),
                $"{settable} maps to {(int)mapped}, which is not a declared {nameof(RecipeStatus)}.");
        }
    }

    /// <summary>
    /// The invariant the enum's own remarks rest on: the serializer accepts a status written as a number, so a
    /// client sending <c>1</c> must keep meaning the same thing on both sides of the mapping.
    /// </summary>
    [Fact]
    public void The_two_enums_agree_on_the_numeric_value_of_every_shared_name()
    {
        foreach (var settable in EverySettableStatus)
        {
            var name = settable.ToString();

            Assert.True(
                Enum.TryParse<RecipeStatus>(name, out var domain),
                $"{name} is settable but is not a {nameof(RecipeStatus)} at all.");
            Assert.True(
                (int)settable == (int)domain,
                $"{name} is {(int)settable} when requested and {(int)domain} when stored. A client that sends "
                    + $"the number rather than the name would be asking for something else.");
        }
    }

    [Fact]
    public void An_omitted_status_means_draft()
    {
        // The default is resolved at the mapping rather than left null, so "what did this request ask for" has
        // one answer — which is what the idempotency fingerprint is computed from.
        Assert.Equal(RecipeStatus.Draft, SettableRecipeStatus.ToDomain(null));
    }

    [Fact]
    public void An_undeclared_number_is_refused_rather_than_mapped_to_a_default()
    {
        // Reachable only by a number the serializer accepted; the validator catches it first on the HTTP path.
        // Mapping it to Draft would silently grant a request nobody made.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SettableRecipeStatus.ToDomain((SettableRecipeStatusViewModel)99));
    }
}
