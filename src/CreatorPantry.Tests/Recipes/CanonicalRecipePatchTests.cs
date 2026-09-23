using System.Text.Json;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The canonical form of an edit: what it normalizes, and which pairs of differently-written edits it makes
/// identical.
/// </summary>
/// <remarks>
/// Two claims at once, as on a create: that a retry written slightly differently still replays, and that two
/// edits treated as the same really would have produced the same recipe. The one this type has to get right
/// that a create never faced is that <em>absent</em> and <em>cleared</em> stay different all the way into
/// the hash — treating them alike would let a retry that quietly dropped a field replay as though it had
/// asked for the same thing.
/// </remarks>
public sealed class CanonicalRecipePatchTests
{
    private const string Token = "AQIDBAUGBwg=";

    private static readonly Guid RecipeId = Guid.NewGuid();

    private static PatchField<T> Set<T>(T value) => PatchField<T>.Submitted(value);

    private static UpdateRecipeViewModel Empty() => new() { ExpectedConcurrencyToken = Token };

    /// <summary>
    /// The serialized fingerprint, which is what actually gets hashed — record equality would compare the
    /// dictionary by reference and report every pair as different.
    /// </summary>
    private static string Fingerprint(UpdateRecipeViewModel model, Guid? recipeId = null) =>
        JsonSerializer.Serialize(
            CanonicalRecipePatch.From(model).Fingerprint(recipeId ?? RecipeId),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // ---- Absent is not cleared ----

    [Fact]
    public void Leaving_a_field_alone_is_a_different_edit_from_clearing_it()
    {
        // The distinction the whole contract rests on, checked where it would be easiest to lose: a
        // dictionary that wrote null for both would make these two hash alike.
        Assert.NotEqual(
            Fingerprint(Empty()),
            Fingerprint(Empty() with { Headnote = Set<string?>(null) }));
    }

    [Fact]
    public void An_unsubmitted_field_contributes_nothing_to_the_fingerprint()
    {
        var json = Fingerprint(Empty() with { Title = Set<string?>("Cake") });

        Assert.Contains("\"title\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"headnote\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cleared_field_appears_as_an_explicit_null()
    {
        var json = Fingerprint(Empty() with { Headnote = Set<string?>(null) });

        Assert.Contains("\"headnote\":null", json, StringComparison.Ordinal);
    }

    // ---- Edits that mean the same thing ----

    [Fact]
    public void Tags_listed_in_another_order_are_the_same_edit()
    {
        Assert.Equal(
            Fingerprint(Empty() with { Tags = Set<IReadOnlyList<string?>?>(["weeknight", "freezer"]) }),
            Fingerprint(Empty() with { Tags = Set<IReadOnlyList<string?>?>(["freezer", "weeknight"]) }));
    }

    [Fact]
    public void Padding_around_text_is_the_same_edit()
    {
        Assert.Equal(
            Fingerprint(Empty() with { Title = Set<string?>("Cake"), Headnote = Set<string?>("A note.") }),
            Fingerprint(Empty() with { Title = Set<string?>("  Cake  "), Headnote = Set<string?>("\tA note.\n") }));
    }

    [Fact]
    public void Submitting_whitespace_is_the_same_edit_as_clearing()
    {
        // Both mean "there is nothing here now", and collapsing them keeps "cleared" to one representation.
        Assert.Equal(
            Fingerprint(Empty() with { Headnote = Set<string?>(null) }),
            Fingerprint(Empty() with { Headnote = Set<string?>("   ") }));
    }

    [Fact]
    public void An_empty_tag_list_is_the_same_edit_as_clearing_the_tags()
    {
        Assert.Equal(
            Fingerprint(Empty() with { Tags = Set<IReadOnlyList<string?>?>(null) }),
            Fingerprint(Empty() with { Tags = Set<IReadOnlyList<string?>?>([]) }));
    }

    // ---- Edits that do not ----

    [Fact]
    public void The_same_fields_against_a_different_state_is_a_different_edit()
    {
        // A caller who re-read the recipe and reused their key is asking for something else: the same words,
        // applied to a recipe that has moved. They are told the key was reused, not handed the old answer.
        Assert.NotEqual(
            Fingerprint(Empty() with { Title = Set<string?>("Cake") }),
            Fingerprint(new UpdateRecipeViewModel
            {
                ExpectedConcurrencyToken = "CAcGBQQDAgE=",
                Title = Set<string?>("Cake"),
            }));
    }

    [Fact]
    public void The_same_edit_to_a_different_recipe_is_a_different_edit()
    {
        Assert.NotEqual(
            Fingerprint(Empty() with { Title = Set<string?>("Cake") }),
            Fingerprint(Empty() with { Title = Set<string?>("Cake") }, Guid.NewGuid()));
    }

    [Fact]
    public void A_different_reason_is_a_different_edit()
    {
        // It is recorded on the version, so replaying the first answer would lose the second one's words.
        Assert.NotEqual(
            Fingerprint(Empty() with { Title = Set<string?>("Cake"), Reason = "Sweeter" }),
            Fingerprint(Empty() with { Title = Set<string?>("Cake"), Reason = "Drier" }));
    }

    // ---- Normalization the merge depends on ----

    [Fact]
    public void Tags_are_deduplicated_by_normalized_name_and_keep_the_first_spelling()
    {
        var patch = CanonicalRecipePatch.From(
            Empty() with { Tags = Set<IReadOnlyList<string?>?>(["Weeknight", "weeknight"]) });

        var tag = Assert.Single(patch.Tags.Value!);
        Assert.Equal("Weeknight", tag.Name);
        Assert.Equal("weeknight", tag.NormalizedName);
    }

    [Fact]
    public void A_requested_status_becomes_a_domain_state()
    {
        var patch = CanonicalRecipePatch.From(
            Empty() with { Status = Set<SettableRecipeStatusViewModel?>(SettableRecipeStatusViewModel.Archived) });

        Assert.True(patch.Status.IsSubmitted);
        Assert.Equal(RecipeStatus.Archived, patch.Status.Value);
    }

    [Fact]
    public void A_status_submitted_as_null_stays_null()
    {
        var patch = CanonicalRecipePatch.From(Empty() with { Status = Set<SettableRecipeStatusViewModel?>(null) });

        // Not folded into Draft, unlike a create's omitted status. A submitted null is a request to clear,
        // which Business refuses — and resolving it here would turn that refusal into a silent
        // un-archiving for any caller that reached Business without the validator.
        Assert.True(patch.Status.IsSubmitted);
        Assert.Null(patch.Status.Value);
    }

    [Fact]
    public void An_unsubmitted_status_stays_unsubmitted()
    {
        // Unlike a create, where an omitted status resolves to Draft. Here an omission means "leave it", and
        // resolving it to anything would silently un-archive recipes on every unrelated edit.
        Assert.False(CanonicalRecipePatch.From(Empty()).Status.IsSubmitted);
    }

    [Fact]
    public void The_token_survives_canonicalization() =>
        Assert.Equal(Token, CanonicalRecipePatch.From(Empty()).ExpectedConcurrencyToken);
}
