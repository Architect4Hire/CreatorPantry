using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through <see cref="AiDiffCalculator.Calculate"/> and
/// <see cref="AiProposalAssembler.Assemble"/> together — pure functions over a hand-built snapshot and answer,
/// no persistence. Covers StaleSource (a pinned version that no longer matches the recipe's current one is
/// refused, never silently rebased) and ProposalAcceptance (a valid diff assembles into a correctly-shaped
/// proposal with real before/after values).
/// </summary>
internal sealed class ProposalAssemblyCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AiEvaluationKind Kind => AiEvaluationKind.ProposalAssembly;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        // A fixture-authoring mistake, not a verdict: an omitted reasonCode would let this fixture pass no
        // matter *why* assembly was refused, silently weakening the regression check the category exists for.
        if (expect.Outcome == "Refused" && expect.ReasonCode is null)
        {
            throw new AiEvaluationException(
                fixture.Identity, "'expect.reasonCode' is required when 'expect.outcome' is 'Refused'.");
        }

        var snapshot = new RecipeSnapshotDocument
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Recipe = new RecipeSnapshotHeader
            {
                Title = input.RecipeTitle,
                Headnote = input.Headnote,
                YieldQuantity = input.YieldQuantity,
            },
        };

        var output = new AiOutputDocument { SchemaVersion = input.SchemaVersion, Changes = input.Changes };
        var diff = AiDiffCalculator.Calculate(snapshot, output);

        if (!diff.Succeeded)
        {
            return Task.FromResult(Verdict(expect, "Refused", diff.Failure!.ReasonCode, diff.Failure.Message));
        }

        var assembly = AiProposalAssembler.Assemble(
            input.WorkspaceId,
            input.OperationId,
            input.PinnedVersionId,
            input.CurrentVersionId,
            output,
            diff.Changes!,
            new AiProposalProvenance(
                input.SchemaVersion, "fixture.harness", "1.0.0", "sha256:fixture", "fixture-provider", "fixture-model", null),
            DateTimeOffset.UnixEpoch);

        if (!assembly.Succeeded)
        {
            return Task.FromResult(Verdict(expect, "Refused", assembly.Failure!.ReasonCode, assembly.Failure.Message));
        }

        if (expect.Outcome != "Assembled")
        {
            return Task.FromResult(AiEvaluationVerdict.Fail(
                $"expected outcome '{expect.Outcome}', but the proposal assembled."));
        }

        var proposal = assembly.Proposal!;

        if (expect.ChangeCount is { } count && proposal.Changes.Count != count)
        {
            return Task.FromResult(AiEvaluationVerdict.Fail(
                $"expected {count} change(s), got {proposal.Changes.Count}."));
        }

        if (expect.FirstChangeBeforeValue is { } before
            && proposal.Changes.FirstOrDefault()?.BeforeValue != before)
        {
            return Task.FromResult(AiEvaluationVerdict.Fail(
                $"expected the first change's before value to be '{before}', got "
                + $"'{proposal.Changes.FirstOrDefault()?.BeforeValue}'."));
        }

        if (expect.FirstChangeAfterValue is { } after
            && proposal.Changes.FirstOrDefault()?.AfterValue != after)
        {
            return Task.FromResult(AiEvaluationVerdict.Fail(
                $"expected the first change's after value to be '{after}', got "
                + $"'{proposal.Changes.FirstOrDefault()?.AfterValue}'."));
        }

        return Task.FromResult(AiEvaluationVerdict.Pass());
    }

    private static AiEvaluationVerdict Verdict(Expect expect, string actualOutcome, string reasonCode, string message)
    {
        if (expect.Outcome != actualOutcome)
        {
            return AiEvaluationVerdict.Fail($"expected outcome '{expect.Outcome}', got '{actualOutcome}' ({message}).");
        }

        if (expect.ReasonCode is { } expectedReason && expectedReason != reasonCode)
        {
            return AiEvaluationVerdict.Fail($"expected reason '{expectedReason}', got '{reasonCode}' ({message}).");
        }

        return AiEvaluationVerdict.Pass();
    }

    private sealed record Input(
        Guid WorkspaceId,
        Guid OperationId,
        Guid? PinnedVersionId,
        Guid? CurrentVersionId,
        string RecipeTitle,
        string? Headnote,
        decimal? YieldQuantity,
        string SchemaVersion,
        IReadOnlyList<AiOutputChange> Changes);

    private sealed record Expect(
        string Outcome,
        string? ReasonCode,
        int? ChangeCount,
        string? FirstChangeBeforeValue,
        string? FirstChangeAfterValue);
}
