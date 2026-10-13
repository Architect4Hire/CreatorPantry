using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through <see cref="AiLongFormOutlineOutputValidator.Validate"/> and, when the plan validates,
/// through the same claim scanning <see cref="LongFormOutlineAiTaskHandler"/> applies before it stores a
/// proposal — no envelope, no gateway, no database.
/// </summary>
/// <remarks>
/// <para>
/// A rejected fixture asserts the reason code. An accepted one may also assert a finding the server must have
/// raised on a section, and words it must <em>not</em> have flagged — and every accepted fixture asserts,
/// without being asked, that each heading and intent was stored exactly as the model wrote it and that no row
/// holds anything a plan has no place for.
/// </para>
/// <para>
/// The shape rules are the content module's own, resolved from its real registration. The recipes a claim is
/// checked against are the fixture's, built into the package shape the handler reads.
/// </para>
/// </remarks>
internal sealed class LongFormOutlineOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // The content module's own registration of its shape rules: stateless singletons, so resolved once.
    private static readonly ILongFormPlanFacade Plans = new ServiceCollection()
        .AddContentModule()
        .BuildServiceProvider()
        .GetRequiredService<ILongFormPlanFacade>();

    public AiEvaluationKind Kind => AiEvaluationKind.LongFormOutlineOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        if (input.Kind is not { } kind)
        {
            throw new AiEvaluationException(fixture.Identity, "'input.kind' is required.");
        }

        var recipes = input.Recipes ?? [];

        var result = AiLongFormOutlineOutputValidator.Validate(
            input.Payload,
            input.ExpectedSchemaVersion,
            new AiLongFormOutlineExpectation(kind, recipes.Count, input.WantedRoles ?? [], Plans));

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => result.Succeeded
                ? Accepted(result.Document!, input, recipes, expect)
                : AiEvaluationVerdict.Fail(
                    $"expected the plan to validate, but it was rejected: {result.Failure!.ReasonCode} "
                    + $"({result.Failure.Message})"),

            "Rejected" => Rejected(fixture.Identity, result, expect),

            _ => throw new AiEvaluationException(
                fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    private static AiEvaluationVerdict Accepted(
        AiLongFormOutlineOutputDocument document, Input input, IReadOnlyList<RecipeInput> recipes, Expect expect)
    {
        var package = Package(input, recipes);
        var (changes, warnings) = LongFormOutlineAiTaskHandler.Translate(document, package);

        var headings = changes.Where(change => change.ChangeKind is AiChangeKind.Add).ToList();

        for (var index = 0; index < document.Sections.Count; index++)
        {
            var section = document.Sections[index];
            var heading = headings[index];
            var fields = changes
                .Where(change => change.TargetId == heading.TargetId && change.FieldName is not null)
                .ToDictionary(change => change.FieldName!, change => change.AfterValue);

            // Never trimmed or re-worded, whatever the fixture is about.
            if (!string.Equals(heading.AfterValue, section.Heading, StringComparison.Ordinal)
                || !string.Equals(fields.GetValueOrDefault(LongFormPlanFields.Intent), section.Intent, StringComparison.Ordinal))
            {
                return AiEvaluationVerdict.Fail($"section {index} was not stored as written.");
            }

            // A plan has a heading, a role, an intent and at most a source. Nothing else has anywhere to go.
            if (fields.Keys.Except([LongFormPlanFields.Role, LongFormPlanFields.Intent, LongFormPlanFields.SourceReferenceId]).Any())
            {
                return AiEvaluationVerdict.Fail($"section {index} was stored with a field a plan does not have.");
            }

            // An item is bound to the recipe at the position the model named; nothing else is bound to one.
            var expectedSource = section.Source is { } position ? package.Recipes[position - 1].ReferenceId.ToString("D") : null;

            if (fields.GetValueOrDefault(LongFormPlanFields.SourceReferenceId) != expectedSource)
            {
                return AiEvaluationVerdict.Fail($"section {index} was not bound to the source it named.");
            }
        }

        foreach (var flag in expect.Flagged ?? [])
        {
            var row = headings[flag.SectionIndex].SortOrder;

            if (!warnings.Any(warning =>
                    warning.ChangeIndex == row
                    && warning.Message.Contains(flag.Containing, StringComparison.OrdinalIgnoreCase)))
            {
                return AiEvaluationVerdict.Fail(
                    $"expected a finding on section {flag.SectionIndex} containing \"{flag.Containing}\", but found: "
                    + (warnings.Count == 0 ? "none" : string.Join(" | ", warnings.Select(warning => warning.Message))));
            }
        }

        foreach (var phrase in expect.NotFlagged ?? [])
        {
            if (warnings.FirstOrDefault(warning =>
                    warning.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase)) is { } unexpected)
            {
                return AiEvaluationVerdict.Fail(
                    $"expected nothing to be flagged about \"{phrase}\", but found: {unexpected.Message}");
            }
        }

        if (expect.NoFindings is true && warnings.Count > 0)
        {
            return AiEvaluationVerdict.Fail(
                "expected no findings, but found: " + string.Join(" | ", warnings.Select(warning => warning.Message)));
        }

        return AiEvaluationVerdict.Pass();
    }

    /// <summary>The fixture's material as the package the handler would have read it from.</summary>
    private static CreativeContextPackage Package(Input input, IReadOnlyList<RecipeInput> recipes) => new(
        Guid.NewGuid(),
        AiTaskType.LongFormOutline,
        Guid.NewGuid(),
        ContextVersion: null,
        new CreativeContextWords(input.WorkingTitle, PictureBrief: null),
        Channels: [],
        Day: null,
        [
            .. recipes.Select(recipe => new CreativeContextRecipeEntry(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                recipe.Title,
                recipe.YieldText,
                recipe.PrepTimeMinutes,
                recipe.CookTimeMinutes,
                RestTimeMinutes: null,
                TotalTimeMinutes: null,
                recipe.Ingredients ?? [],
                recipe.Steps ?? [],
                0,
                0)),
        ],
        Concepts: [],
        Pictures: [],
        Prompts: [],
        Dropped: [],
        Omissions: [],
        EstimatedTokens: 0,
        Checksum: "sha256:fixture",
        AssembledAt: DateTimeOffset.UnixEpoch);

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity, AiOutputValidationOutcome<AiLongFormOutlineOutputDocument> result, Expect expect)
    {
        if (expect.ReasonCode is null)
        {
            throw new AiEvaluationException(
                fixtureIdentity, "'expect.reasonCode' is required when 'expect.outcome' is 'Rejected'.");
        }

        if (result.Succeeded)
        {
            return AiEvaluationVerdict.Fail("expected the plan to be rejected, but it validated.");
        }

        if (result.Failure!.ReasonCode != expect.ReasonCode)
        {
            return AiEvaluationVerdict.Fail(
                $"expected reason '{expect.ReasonCode}', got '{result.Failure.ReasonCode}' "
                + $"({result.Failure.Message}).");
        }

        if (expect.Category is { } category && result.Failure.Category != category)
        {
            return AiEvaluationVerdict.Fail(
                $"expected category '{category}', got '{result.Failure.Category}'.");
        }

        return AiEvaluationVerdict.Pass();
    }

    private sealed record Input(
        string? Payload,
        string ExpectedSchemaVersion,
        LongFormKind? Kind,
        IReadOnlyList<LongFormSectionRole>? WantedRoles = null,
        string? WorkingTitle = null,
        IReadOnlyList<RecipeInput>? Recipes = null);

    private sealed record RecipeInput(
        string Title,
        string? YieldText = null,
        int? PrepTimeMinutes = null,
        int? CookTimeMinutes = null,
        IReadOnlyList<string>? Ingredients = null,
        IReadOnlyList<string>? Steps = null);

    private sealed record Flag(int SectionIndex, string Containing);

    private sealed record Expect(
        string Outcome,
        string? ReasonCode = null,
        AiFailureCategory? Category = null,
        IReadOnlyList<Flag>? Flagged = null,
        IReadOnlyList<string>? NotFlagged = null,
        bool? NoFindings = null);
}
