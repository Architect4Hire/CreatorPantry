using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through <see cref="AiSeoPackageOutputValidator.Validate"/> under the default rules and, when the
/// answer is accepted and the fixture declares the recipe's facts, through <see cref="AiSeoClaimScanner"/> — no
/// envelope, no gateway, no database. Covers SchemaValidity, RefusalSafety, PromptInjection and accessibility
/// for RCPUB-002's own document.
/// </summary>
internal sealed class SeoPackageOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AiEvaluationKind Kind => AiEvaluationKind.SeoPackageOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        var result = AiSeoPackageOutputValidator.Validate(input.Payload, input.ExpectedSchemaVersion, new SeoRules());

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => result.Succeeded
                ? Findings(fixture.Identity, result.Document!, input, expect)
                : AiEvaluationVerdict.Fail(
                    $"expected the answer to validate, but it was rejected: {result.Failure!.ReasonCode} ({result.Failure.Message})"),

            "Rejected" => Rejected(fixture.Identity, result, expect),

            _ => throw new AiEvaluationException(fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    private static AiEvaluationVerdict Findings(
        string fixtureIdentity, AiSeoPackageOutputDocument document, Input input, Expect expect)
    {
        if (expect.Warnings is null)
        {
            return AiEvaluationVerdict.Pass();
        }

        if (input.Source is null)
        {
            throw new AiEvaluationException(fixtureIdentity, "'input.source' is required when 'expect.warnings' is declared.");
        }

        var facts = AiEditorialSourceFacts.Of(input.Source.Numbers ?? [], input.Source.Prose, input.Source.StorageNotes);
        var captions = (input.Assets ?? []).ToDictionary(asset => asset.Id, asset => asset.Caption);
        var found = AiSeoClaimScanner.Scan(document.Sections, facts, captions, input.RecipeTitle ?? "Recipe")
            .Select(finding => finding.Code)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        var wanted = expect.Warnings.Distinct().Order(StringComparer.Ordinal).ToArray();

        return found.SequenceEqual(wanted)
            ? AiEvaluationVerdict.Pass()
            : AiEvaluationVerdict.Fail($"expected the scanner to report [{string.Join(", ", wanted)}], got [{string.Join(", ", found)}].");
    }

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity, AiOutputValidationOutcome<AiSeoPackageOutputDocument> result, Expect expect)
    {
        if (expect.ReasonCode is null)
        {
            throw new AiEvaluationException(fixtureIdentity, "'expect.reasonCode' is required when 'expect.outcome' is 'Rejected'.");
        }

        if (result.Succeeded)
        {
            return AiEvaluationVerdict.Fail("expected the answer to be rejected, but it validated.");
        }

        return result.Failure!.ReasonCode != expect.ReasonCode
            ? AiEvaluationVerdict.Fail($"expected reason '{expect.ReasonCode}', got '{result.Failure.ReasonCode}' ({result.Failure.Message}).")
            : AiEvaluationVerdict.Pass();
    }

    private sealed record Input(string? Payload, string ExpectedSchemaVersion, SourceFacts? Source, Asset[]? Assets, string? RecipeTitle);

    private sealed record SourceFacts(string[]? Numbers, string? Prose, string? StorageNotes);

    private sealed record Asset(Guid Id, string? Caption);

    private sealed record Expect(string Outcome, string? ReasonCode, string[]? Warnings);
}
