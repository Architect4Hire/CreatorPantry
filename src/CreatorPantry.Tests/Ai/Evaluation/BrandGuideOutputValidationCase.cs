using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through <see cref="AiBrandGuideOutputValidator.Validate"/> against a declared request
/// context, and then through <see cref="AiBrandGuideClaimScanner.Scan"/> — no envelope, no gateway, no
/// database. Covers SchemaValidity, RefusalSafety, StaleSource and WorkspaceIsolation for 11A.17's own
/// document, which is not shaped like a recipe diff and so cannot run through
/// <see cref="OutputValidationCase"/>.
/// </summary>
/// <remarks>
/// <para>
/// The request context is what makes this capability's restrictions demonstrable rather than merely described: a
/// citation is only valid against the passages one request actually supplied, so a fixture declares those
/// passages and the validator is given the same view the handler would give it.
/// </para>
/// <para>
/// <strong>Isolation here is structural, not a database check.</strong> A fixture proving that another
/// workspace's passage cannot be cited does so by offering a passage set that does not contain it — which is the
/// real guarantee, since the handler's set comes from a facade reading under the workspace query filter. What a
/// fixture cannot show is that the filter itself works; <c>BrandSourceChunkSqlServerTests</c> does that.
/// </para>
/// </remarks>
internal sealed class BrandGuideOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AiEvaluationKind Kind => AiEvaluationKind.BrandGuideOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        var context = Context(input);
        var result = AiBrandGuideOutputValidator.Validate(input.Payload, input.ExpectedSchemaVersion, context);

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => Accepted(fixture.Identity, result, expect, input),
            "Rejected" => Rejected(fixture.Identity, result, expect),
            _ => throw new AiEvaluationException(
                fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    /// <summary>
    /// What the request offered. A fixture that declares no dimensions offers every one, so a case about
    /// citations does not have to restate the dimension list to stay valid.
    /// </summary>
    private static AiBrandGuideRequestContext Context(Input input)
    {
        var dimensions = input.Dimensions is { Count: > 0 }
            ? input.Dimensions.Select(name => AiBrandGuideDimensionCatalog.Parse(name)
                ?? throw new AiEvaluationException("brand-guide-fixture", $"'{name}' is not a dimension.")).ToHashSet()
            : Enum.GetValues<AiBrandGuideDimension>()
                .Where(dimension => dimension is not AiBrandGuideDimension.Unspecified)
                .ToHashSet();

        return new AiBrandGuideRequestContext(
            dimensions,
            (input.ChannelKeys ?? []).ToHashSet(StringComparer.Ordinal),
            (input.Passages ?? []).ToDictionary(passage => passage.PassageId, passage => passage.Text));
    }

    private static AiEvaluationVerdict Accepted(
        string fixtureIdentity,
        AiOutputValidationOutcome<AiBrandGuideOutputDocument> result,
        Expect expect,
        Input input)
    {
        if (!result.Succeeded)
        {
            return AiEvaluationVerdict.Fail(
                $"expected the answer to validate, but it was rejected: {result.Failure!.ReasonCode} "
                + $"({result.Failure.Message})");
        }

        if (expect.Findings is null)
        {
            return AiEvaluationVerdict.Pass();
        }

        var document = result.Document!;

        // The handler's own derivation, not a copy of it: a fixture that rebuilt this itself would keep passing
        // after the real rule changed.
        var findings = AiBrandGuideClaimScanner.Scan(
            document,
            AiBrandGuideClaimScanner.Unsupported(document),
            input.Passages?.Count ?? 0,
            input.UnavailableSources ?? 0);

        var reported = findings.Select(finding => finding.Code).ToHashSet(StringComparer.Ordinal);
        var missing = expect.Findings.Where(code => !reported.Contains(code)).ToList();

        if (missing.Count > 0)
        {
            return AiEvaluationVerdict.Fail(
                $"expected finding(s) [{string.Join(", ", missing)}], got [{string.Join(", ", reported)}].");
        }

        // Exact, not "at least": a scanner that fired on something the fixture did not expect is reporting
        // something about this answer nobody has reviewed, which is as much a defect as a missed finding.
        var unexpected = reported.Where(code => !expect.Findings.Contains(code)).ToList();

        return unexpected.Count > 0
            ? AiEvaluationVerdict.Fail($"unexpected finding(s) [{string.Join(", ", unexpected)}].")
            : AiEvaluationVerdict.Pass();
    }

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity, AiOutputValidationOutcome<AiBrandGuideOutputDocument> result, Expect expect)
    {
        if (expect.ReasonCode is null)
        {
            throw new AiEvaluationException(
                fixtureIdentity, "'expect.reasonCode' is required when 'expect.outcome' is 'Rejected'.");
        }

        if (result.Succeeded)
        {
            return AiEvaluationVerdict.Fail("expected the answer to be rejected, but it validated.");
        }

        if (result.Failure!.ReasonCode != expect.ReasonCode)
        {
            return AiEvaluationVerdict.Fail(
                $"expected reason '{expect.ReasonCode}', got '{result.Failure.ReasonCode}' ({result.Failure.Message}).");
        }

        return expect.Category is { } category && result.Failure.Category != category
            ? AiEvaluationVerdict.Fail($"expected category '{category}', got '{result.Failure.Category}'.")
            : AiEvaluationVerdict.Pass();
    }

    /// <param name="Passages">What the request supplied. A citation naming anything else must be refused.</param>
    /// <param name="UnavailableSources">
    /// How many selected documents could supply nothing, so the unavailable-source finding can be demonstrated
    /// without a database.
    /// </param>
    private sealed record Input(
        string? Payload,
        string ExpectedSchemaVersion,
        List<string>? Dimensions,
        List<string>? ChannelKeys,
        List<Passage>? Passages,
        int? UnavailableSources);

    private sealed record Passage(Guid PassageId, string Text);

    /// <param name="Findings">
    /// The exact scanner codes this answer must produce. Omitted means the fixture is not about the scanner.
    /// </param>
    private sealed record Expect(
        string Outcome, string? ReasonCode, AiFailureCategory? Category, List<string>? Findings);
}
