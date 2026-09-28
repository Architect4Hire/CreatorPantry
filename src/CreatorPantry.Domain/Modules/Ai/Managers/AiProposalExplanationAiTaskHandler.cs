using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.ProposalExplanation"/> (AIREC-008): a concise, creator-facing explanation of an
/// existing, already-persisted proposal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deterministic templating, never a model call.</strong> Every explanation item is built by reading
/// the source proposal's own <see cref="AiStructuredChange"/> and <see cref="AiWarning"/> rows and restating
/// their recorded fields — a field name, a before/after value, a warning's own message. Nothing here can add a
/// change or a claim the source proposal does not already carry, because there is no model in the loop that
/// could invent one.
/// </para>
/// <para>
/// <strong>Shaped like <see cref="RecipeReviewAiTaskHandler"/>, not like a diff-producing handler.</strong> It
/// addresses no change to a recipe, so <see cref="AiDiffCalculator"/> is never called and the operation runs in
/// <see cref="AiOperationScope.Advisory"/>. Each item is flattened into
/// <see cref="AiChangeTargetKind.ProposalExplanationItem"/> rows, deliberately absent from
/// <see cref="AiChangeApplicability"/>.
/// </para>
/// <para>
/// <strong>Re-reads the source rather than trusting the request that queued it.</strong> Business already
/// checked that the source has a proposal when the operation was requested, but a worker can run long after
/// that; re-checking here is the same defensive read <see cref="RecipeReviewAiTaskHandler"/> makes of its own
/// pinned snapshot, and is this handler's own missing-link refusal.
/// </para>
/// </remarks>
internal sealed class AiProposalExplanationAiTaskHandler(
    IAiOperationDataLayer operations, IClock clock) : IAiTaskHandler
{
    private const string ProviderName = "deterministic";

    private const string ModelName = "template";

    private const string AlgorithmVersion = "1.0.0";

    private const string SchemaVersion = "ai.proposalExplanation.v1";

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Inputs?.TryGetValue(AiProposalExplanationInputs.SourceRequestId, out var raw) != true
            || !Guid.TryParse(raw, out var sourceRequestId))
        {
            // Unreachable through the request seam, which always writes this field. Refused rather than
            // assumed: an explanation with nothing to explain has nothing to do.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "An explanation names the request whose proposal it explains.",
                []);
        }

        var source = await operations.GetWithProposalAsync(sourceRequestId, cancellationToken);

        if (source?.Proposal is null)
        {
            // The missing-link case: the source this operation named no longer resolves to a proposal, whether
            // because it never reached one or because it has since become unreachable. Either way there is
            // nothing here to link an explanation item to, and none is written.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.DomainInvalid,
                "The proposal this explanation was asked about is no longer available.",
                []);
        }

        var (changes, warnings) = Translate(source.Proposal);
        var output = new AiOutputDocument { SchemaVersion = SchemaVersion, Warnings = warnings };

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,

            // Not a staleness check in the sense a diff-producing handler means it: the source proposal is
            // immutable, so there is nothing here that could go stale. Passing the same inherited value twice,
            // exactly as RecipeReviewAiTaskHandler does, makes AiProposalAssembler's check trivially pass.
            context.RecipeVersionId,
            context.RecipeVersionId,
            output,
            changes,
            new AiProposalProvenance(
                SchemaVersion, AiTaskCatalog.ProposalExplanation, AlgorithmVersion, "none", ProviderName, ModelName, null),
            clock.UtcNow);

        return assembly.Succeeded
            ? AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, [])
            : AiTaskHandlerOutcome.ForFailure(assembly.Failure!.Category, assembly.Failure.Message, []);
    }

    /// <summary>
    /// One explanation item per target the source proposal addressed — grouping its <c>Set</c> rows back
    /// together with the <c>Add</c> row they belong to — plus one item for the source's proposal-level
    /// warnings, and a single "nothing changed" item when the source proposed nothing at all.
    /// </summary>
    /// <remarks>
    /// A linked warning becomes a real <see cref="AiOutputWarning"/> rather than a restated string, so
    /// <see cref="AiProposalAssembler"/> stores it as its own <see cref="AiWarning"/> row carrying the source
    /// warning's own <see cref="AiWarningKind"/> — a <see cref="AiWarningKind.Rationale"/> or
    /// <see cref="AiWarningKind.Limitation"/> note stays distinguishable from a genuine
    /// <see cref="AiWarningKind.SafetyCaution"/> instead of being flattened into one undifferentiated field, and
    /// the explanation's own <c>Warnings</c> list is populated the same way every other capability's is.
    /// </remarks>
    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(AiProposal source)
    {
        var changes = new List<AiResolvedChange>();
        var warnings = new List<AiOutputWarning>();

        var groups = source.Changes
            .OrderBy(change => change.SortOrder)
            .GroupBy(change => change.TargetId);

        foreach (var group in groups)
        {
            var sourceChanges = group.OrderBy(change => change.SortOrder).ToList();
            var targetId = Guid.NewGuid();
            var addRowIndex = changes.Count;
            var summary = $"{sourceChanges[0].TargetKind} {DescribeAction(sourceChanges)}.";

            changes.Add(new AiResolvedChange(
                AiChangeKind.Add,
                AiChangeTargetKind.ProposalExplanationItem,
                targetId,
                FieldName: null,
                BeforeValue: null,
                summary,
                ProposedPosition: addRowIndex,
                addRowIndex));

            AddSet(
                changes, targetId, AiProposalExplanationFields.SourceChangeIds, Join(sourceChanges.Select(c => c.Id)));
            AddSet(changes, targetId, AiProposalExplanationFields.TargetKind, sourceChanges[0].TargetKind.ToString());
            AddSet(
                changes,
                targetId,
                AiProposalExplanationFields.Detail,
                string.Join("; ", sourceChanges.Select(DescribeChange).Where(detail => detail is not null)));

            LinkWarnings(
                changes,
                warnings,
                targetId,
                addRowIndex,
                source.Warnings.Where(warning => sourceChanges.Any(c => c.Id == warning.AiStructuredChangeId)));
        }

        var generalWarnings = source.Warnings.Where(warning => warning.AiStructuredChangeId is null).ToList();

        if (generalWarnings.Count > 0)
        {
            var targetId = Guid.NewGuid();
            var addRowIndex = changes.Count;

            changes.Add(new AiResolvedChange(
                AiChangeKind.Add,
                AiChangeTargetKind.ProposalExplanationItem,
                targetId,
                FieldName: null,
                BeforeValue: null,
                "General notes about this proposal.",
                ProposedPosition: addRowIndex,
                addRowIndex));

            LinkWarnings(changes, warnings, targetId, addRowIndex, generalWarnings);
        }

        if (changes.Count == 0)
        {
            var targetId = Guid.NewGuid();

            changes.Add(new AiResolvedChange(
                AiChangeKind.Add,
                AiChangeTargetKind.ProposalExplanationItem,
                targetId,
                FieldName: null,
                BeforeValue: null,
                "No changes were proposed.",
                ProposedPosition: 0,
                0));
        }

        return (changes, warnings);
    }

    /// <summary>
    /// Records which of the source's warnings this item describes (for exact id traceability) and re-emits
    /// each one as its own <see cref="AiOutputWarning"/>, carrying its own <see cref="AiWarningKind"/> and its
    /// message verbatim — never merged into one field that would erase the difference between a caution and a
    /// rationale.
    /// </summary>
    private static void LinkWarnings(
        List<AiResolvedChange> changes,
        List<AiOutputWarning> warnings,
        Guid targetId,
        int addRowIndex,
        IEnumerable<AiWarning> sourceWarnings)
    {
        var ordered = sourceWarnings.OrderBy(warning => warning.SortOrder).ToList();

        if (ordered.Count == 0)
        {
            return;
        }

        AddSet(changes, targetId, AiProposalExplanationFields.SourceWarningIds, Join(ordered.Select(w => w.Id)));

        warnings.AddRange(ordered.Select(warning => new AiOutputWarning
        {
            Kind = warning.Kind,
            Message = warning.Message,
            ChangeIndex = addRowIndex,
        }));
    }

    private static string DescribeAction(IReadOnlyList<AiStructuredChange> group)
    {
        if (group.Any(change => change.ChangeKind is AiChangeKind.Add))
        {
            return "added";
        }

        if (group.Any(change => change.ChangeKind is AiChangeKind.Remove))
        {
            return "removed";
        }

        if (group.Any(change => change.ChangeKind is AiChangeKind.Move))
        {
            return "reordered";
        }

        return "updated";
    }

    /// <summary>
    /// One factual sentence restating exactly what a single stored row already says — never a new fact, only
    /// the field name and the before/after values <see cref="AiDiffCalculator"/> already resolved for it.
    /// </summary>
    private static string? DescribeChange(AiStructuredChange change) => change.ChangeKind switch
    {
        AiChangeKind.Set when change.FieldName is not null =>
            $"{change.FieldName} changed from {Quote(change.BeforeValue)} to {Quote(change.AfterValue)}",
        AiChangeKind.Add when change.AfterValue is not null => $"Added {Quote(change.AfterValue)}",
        AiChangeKind.Add => null,
        AiChangeKind.Remove => "Removed",
        AiChangeKind.Move when change.ProposedPosition is { } position => $"Reordered to position {position}",
        AiChangeKind.Move => "Reordered",
        _ => null,
    };

    private static string Quote(string? value) => value is null ? "(none)" : $"'{value}'";

    private static string Join(IEnumerable<Guid> ids) => string.Join(",", ids);

    private static void AddSet(List<AiResolvedChange> changes, Guid targetId, string field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        changes.Add(new AiResolvedChange(
            AiChangeKind.Set,
            AiChangeTargetKind.ProposalExplanationItem,
            targetId,
            field,
            BeforeValue: null,
            value,
            ProposedPosition: null,
            changes.Count));
    }
}

/// <summary>
/// The field names an explanation item's <see cref="AiChangeKind.Set"/> rows use. Not governed by
/// <see cref="AiDiffFields"/> — that table is the recipe-facing settable-field allow-list, and
/// <see cref="AiChangeTargetKind.ProposalExplanationItem"/> is deliberately absent from it.
/// </summary>
internal static class AiProposalExplanationFields
{
    /// <summary>Comma-separated ids of the source proposal's own <see cref="AiStructuredChange"/> rows this item describes.</summary>
    public const string SourceChangeIds = "sourceChangeIds";

    public const string TargetKind = "targetKind";

    /// <summary>The factual sentence(s), restating only recorded field/value pairs.</summary>
    public const string Detail = "detail";

    /// <summary>
    /// Comma-separated ids of the source proposal's own <see cref="AiWarning"/> rows this item describes. The
    /// warnings themselves are not restated here — they become this proposal's own <see cref="AiWarning"/>
    /// rows, kept alongside this item via <see cref="AiWarning.AiStructuredChangeId"/>, so a caution and a
    /// rationale stay distinguishable by their own <see cref="AiWarningKind"/>.
    /// </summary>
    public const string SourceWarningIds = "sourceWarningIds";
}
