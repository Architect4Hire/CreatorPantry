extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-002's acceptance contract: what a client may send, and what the route answers when it will not do.
/// </summary>
public sealed class AiDraftAcceptanceContractTests
{
    private const string Route = "/api/v1/workspaces/workspace-a/recipe-draft-requests";

    // ---- field shape -----------------------------------------------------------------------------------

    /// <summary>
    /// The body that creates a recipe. It names a decision, the parts being taken, the creator's own wording
    /// and their opinion — and nothing that could say <em>where</em> any of it goes.
    /// </summary>
    [Fact]
    public void The_acceptance_carries_only_a_decision_a_selection_edits_and_feedback()
    {
        var fields = typeof(AiDraftAcceptanceViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["AcceptedChangeIds", "Comment", "Decision", "Edits", "WasHelpful"], fields);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("recipe")]
    [InlineData("prompt")]
    [InlineData("template")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("system")]
    [InlineData("status")]
    public void The_acceptance_cannot_name_a_workspace_a_recipe_or_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(AiDraftAcceptanceViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A rewrite says which part of the draft it replaces and what it says. It cannot say which recipe field
    /// to put it in — the server decides that from the change it names.
    /// </summary>
    [Fact]
    public void A_rewrite_names_a_change_a_field_and_a_value_and_nothing_else()
    {
        var fields = typeof(AiDraftFieldEditViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ChangeId", "Field", "Value"], fields);
    }

    // ---- validator -------------------------------------------------------------------------------------

    private static FluentValidation.Results.ValidationResult Validate(AiDraftAcceptanceViewModel model) =>
        new AiDraftAcceptanceViewModelValidator().Validate(model);

    private static AiDraftAcceptanceViewModel AcceptAll(params Guid[] ids) =>
        new() { Decision = AiDispositionDecision.AcceptAll, AcceptedChangeIds = ids };

    [Fact]
    public void A_decision_that_says_nothing_is_not_a_decision()
    {
        Assert.False(Validate(new AiDraftAcceptanceViewModel()).IsValid);
    }

    [Fact]
    public void A_decision_outside_the_enum_is_refused()
    {
        var model = new AiDraftAcceptanceViewModel { Decision = (AiDispositionDecision)99 };

        Assert.False(Validate(model).IsValid);
    }

    [Fact]
    public void An_accept_all_naming_changes_validates()
    {
        Assert.True(Validate(AcceptAll(Guid.NewGuid(), Guid.NewGuid())).IsValid);
    }

    [Theory]
    [InlineData(AiDispositionDecision.AcceptAll)]
    [InlineData(AiDispositionDecision.AcceptSelected)]
    public void An_acceptance_naming_nothing_is_refused(AiDispositionDecision decision)
    {
        Assert.False(Validate(new AiDraftAcceptanceViewModel { Decision = decision }).IsValid);
        Assert.False(
            Validate(new AiDraftAcceptanceViewModel { Decision = decision, AcceptedChangeIds = [] }).IsValid);
    }

    [Fact]
    public void An_empty_guid_is_not_a_part_of_a_draft()
    {
        Assert.False(Validate(AcceptAll(Guid.Empty)).IsValid);
    }

    [Fact]
    public void The_same_part_cannot_be_named_twice()
    {
        var id = Guid.NewGuid();

        Assert.False(Validate(AcceptAll(id, id)).IsValid);
    }

    [Fact]
    public void A_rejection_validates_on_its_own()
    {
        Assert.True(Validate(new AiDraftAcceptanceViewModel { Decision = AiDispositionDecision.Reject }).IsValid);
    }

    /// <summary>
    /// Business coerces a rejection's selection to empty, so this validator is the only thing that refuses a
    /// request which cannot decide what it is asking for — and one of the two readings creates a recipe.
    /// </summary>
    [Fact]
    public void A_rejection_that_also_accepts_parts_is_refused()
    {
        var model = new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.Reject,
            AcceptedChangeIds = [Guid.NewGuid()],
        };

        Assert.False(Validate(model).IsValid);
    }

    [Fact]
    public void A_rejection_carrying_rewrites_is_refused()
    {
        var model = new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.Reject,
            Edits = [Edit(Guid.NewGuid(), "title", "Mine")],
        };

        Assert.False(Validate(model).IsValid);
    }

    [Fact]
    public void A_rewrite_must_name_a_change()
    {
        var model = AcceptAll(Guid.NewGuid());
        model.Edits = [Edit(Guid.Empty, "title", "Mine")];

        Assert.False(Validate(model).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_rewrite_must_name_a_field(string? field)
    {
        var model = AcceptAll(Guid.NewGuid());
        model.Edits = [Edit(Guid.NewGuid(), field, "Mine")];

        Assert.False(Validate(model).IsValid);
    }

    /// <summary>
    /// Blank is refused rather than read as "clear this field": a deliberate clearing and a slip are
    /// indistinguishable, and one of the readings drops content the creator reviewed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_rewrite_must_carry_a_value(string? value)
    {
        var model = AcceptAll(Guid.NewGuid());
        model.Edits = [Edit(Guid.NewGuid(), "title", value)];

        Assert.False(Validate(model).IsValid);
    }

    [Fact]
    public void A_rewrite_over_the_stored_bound_is_refused()
    {
        var model = AcceptAll(Guid.NewGuid());
        model.Edits = [Edit(Guid.NewGuid(), "title", new string('x', AiPolicy.ChangeValueMaxLength + 1))];

        Assert.False(Validate(model).IsValid);
    }

    [Fact]
    public void The_same_field_cannot_be_rewritten_twice()
    {
        var id = Guid.NewGuid();
        var model = AcceptAll(id);
        model.Edits = [Edit(id, "title", "One"), Edit(id, "title", "Two")];

        Assert.False(Validate(model).IsValid);
    }

    /// <summary>Two fields of the same row is an ordinary thing to do, and is not the duplicate above.</summary>
    [Fact]
    public void Two_fields_of_one_part_may_both_be_rewritten()
    {
        var id = Guid.NewGuid();
        var model = AcceptAll(id);
        model.Edits = [Edit(id, "displayText", "One"), Edit(id, "unitText", "cups")];

        Assert.True(Validate(model).IsValid);
    }

    [Fact]
    public void A_comment_over_its_bound_is_refused()
    {
        var model = AcceptAll(Guid.NewGuid());
        model.Comment = new string('x', AiPolicy.MessageMaxLength + 1);

        Assert.False(Validate(model).IsValid);
    }

    private static AiDraftFieldEditViewModel Edit(Guid changeId, string? field, string? value) =>
        new() { ChangeId = changeId, Field = field, Value = value };

    // ---- error codes -----------------------------------------------------------------------------------

    [Fact]
    public void The_error_codes_are_stable_and_distinct()
    {
        var codes = typeof(AiDraftAcceptanceErrors).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.StartsWith("ai.", code, StringComparison.Ordinal));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The route documents a 409, and it is a 409 only because the code happens to end in <c>.conflict</c> —
    /// <c>ProblemResults.StatusFor</c> reads the suffix. Renaming that constant would silently turn every
    /// already-decided answer into a 400.
    /// </summary>
    [Theory]
    [InlineData(nameof(AiDraftAcceptanceErrors.RequestInvalid), 400)]
    [InlineData(nameof(AiDraftAcceptanceErrors.SelectionInvalid), 400)]
    [InlineData(nameof(AiDraftAcceptanceErrors.DraftNotFound), 404)]
    [InlineData(nameof(AiDraftAcceptanceErrors.DraftDecided), 409)]
    [InlineData(nameof(AiDraftAcceptanceErrors.AcceptanceForbidden), 403)]
    public void Each_error_code_maps_to_the_status_the_route_documents(string name, int expected)
    {
        var code = (string)typeof(AiDraftAcceptanceErrors).GetField(name)!.GetRawConstantValue()!;

        Assert.Equal(expected, ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(code));
    }

    // ---- the route -------------------------------------------------------------------------------------

    /// <summary>The only route in this trio that writes a recipe, so its edge is worth pinning.</summary>
    [Fact]
    public async Task Accepting_a_draft_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{Route}/{Guid.NewGuid()}/acceptance",
            new AiDraftAcceptanceViewModel { Decision = AiDispositionDecision.Reject },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
