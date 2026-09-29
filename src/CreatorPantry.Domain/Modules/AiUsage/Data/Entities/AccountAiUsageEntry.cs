using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Entities;

/// <summary>
/// What one provider attempt cost the <em>account</em> that asked for it. One row per attempt, platform-scoped
/// and never filtered by workspace.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists when <c>AiExecutionMetadata</c> already records an attempt.</strong> Usage is
/// measured per account, not per workspace (USAGE-001): one person who belongs to five workspaces has one
/// balance, and they spend it from whichever workspace they happen to be working in. Neither existing table
/// can answer that. <c>AiExecutionMetadata</c> is <see cref="IWorkspaceOwned"/>, so
/// <c>WorkspaceOwnershipConvention</c> filters it and summing one person across workspaces would mean
/// <c>IgnoreQueryFilters()</c> — which tenancy.md prohibits, and which would sit on the hottest read path in
/// the feature rather than in a documented maintenance corner. <c>AiOperation.RequestedByMembershipId</c>
/// makes it worse: membership is the workspace-scoped identity, so one person is five different ids there.
/// </para>
/// <para>
/// <strong>So this is keyed by the Identity account and carries no query filter, and that is safe for exactly
/// one reason: the row holds counts and never content.</strong> Every column below is an id, an instant, a
/// count, a flag, an enum, or a bounded provider identifier. There is no recipe title, no prompt body, no
/// proposal text, and — deliberately — no free-text column of any kind, not even a sanitized failure summary.
/// <c>AiExecutionMetadata.FailureSummary</c> already carries that, inside the workspace filter, where it
/// belongs; a free-text column on an unfiltered table is where creator content eventually lands.
/// <c>AccountAiUsageEntryModelShapeTests</c> makes that a build failure rather than a review comment. The
/// absence of the filter stops being defensible the moment a content-bearing column is added, so no column
/// ever may be.
/// </para>
/// <para>
/// <strong><see cref="WorkspaceId"/> is a reporting dimension, not an owner.</strong> It groups a breakdown
/// (USAGE-008); it never authorizes a read, and this entity is not <see cref="IWorkspaceOwned"/> so nothing
/// stamps or filters it. The read seam authorizes by account and then withholds the <em>name</em> of a
/// workspace the account no longer belongs to, while leaving its historical counts intact.
/// </para>
/// <para>
/// <strong>It declares no foreign keys at all</strong> — not to <c>Workspace</c>, not to <c>AiOperation</c>,
/// not to <c>ApplicationUser</c>. Deleting a workspace does not unspend the tokens, and every one of those
/// relationships would cascade an account's usage history away (9A.3). <c>AiOperation</c> makes the same trade
/// for the same reason with <c>RequestedByMembershipId</c>. The useful side effect is that this is the one
/// AI-adjacent table no cascade can reach, which closes <see cref="IImmutableRecord"/>'s one documented leak
/// here: the references are ids validated by the recording seam, not relationships enforced by the database.
/// </para>
/// <para>
/// Immutable. An attempt cost what it cost.
/// </para>
/// </remarks>
public class AccountAiUsageEntry : IImmutableRecord
{
    public Guid Id { get; set; }

    /// <summary>
    /// The Identity account this attempt is charged to.
    /// </summary>
    /// <remarks>
    /// Resolved server-side from the operation's membership. Never a request field, a query-string value, an
    /// unsigned header, or anything a model supplied (USAGE-001, ai.md).
    /// </remarks>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>
    /// When the attempt completed, UTC. The instant a quota period is assigned from, so it is the attempt's
    /// own end rather than the moment this row happened to be written.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The operation this attempt belonged to. An id, not a foreign key — see the type's remarks.</summary>
    public Guid AiOperationId { get; set; }

    /// <summary>
    /// Sequential from 1 within one operation, matching the attempt numbering
    /// <c>AiExecutionMetadata</c> uses. Paired with <see cref="AiOperationId"/> it is what makes one attempt
    /// post at most one entry, so a duplicate delivery posts once.
    /// </summary>
    public int AttemptNumber { get; set; }

    /// <summary>Which capability the work was, so a creator's consumption can be broken down by task.</summary>
    /// <remarks>
    /// The AI module's own enum rather than a copy or a string: this breakdown has to line up with the tasks
    /// that produced it, and a second vocabulary would drift the first time a capability was added. Permitted
    /// to cross the module boundary as the ServiceModel-surface allowance in <c>ModuleBoundaryTests</c>.
    /// </remarks>
    public AiTaskType TaskType { get; set; }

    /// <summary>
    /// The workspace the work was done in, as a reporting dimension only.
    /// </summary>
    /// <remarks>
    /// It groups a breakdown and nothing else. It never authorizes a read, it is never the predicate a query
    /// relies on for scope, and because this entity is not <see cref="IWorkspaceOwned"/> nothing stamps or
    /// filters it. A column with this name on an unfiltered table is unusual enough to be worth saying twice:
    /// it is a dimension, not an owner.
    /// </remarks>
    public Guid WorkspaceId { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public string? ModelDeployment { get; set; }

    /// <summary>Null when the provider did not report usage, which is not the same as zero (USAGE-005).</summary>
    public int? InputTokens { get; set; }

    /// <inheritdoc cref="InputTokens"/>
    public int? OutputTokens { get; set; }

    /// <summary>
    /// The total the provider reported, and never a sum computed from the two columns above.
    /// </summary>
    /// <remarks>
    /// Providers report totals that are legitimately not input plus output — cached prompt tokens, reasoning
    /// tokens, tokens counted against a different rate. A derived column would quietly disagree with the
    /// invoice and would make "we know the total" indistinguishable from "we added up what we happened to be
    /// told". Null when unreported, on the same reading as <see cref="InputTokens"/>.
    /// </remarks>
    public int? TotalTokens { get; set; }

    /// <summary>
    /// What this attempt is believed to have cost, on the same footing as
    /// <c>AiExecutionMetadata.EstimatedCost</c>: an estimate derived from counts and a price that is not
    /// authoritative here. Nothing bills from this column.
    /// </summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>
    /// Whether this attempt counts against the account's allowance.
    /// </summary>
    /// <remarks>
    /// Decided by the recording seam and recorded here rather than inferred at read time, so that a later
    /// change of policy cannot retroactively re-price a settled period. A diagnostic task or an attempt
    /// abandoned before the provider was reached is the obvious non-billable case.
    /// </remarks>
    public bool IsBillable { get; set; }

    /// <summary>
    /// Whether the provider reported usage at all (USAGE-005).
    /// </summary>
    /// <remarks>
    /// Not redundant with the nullable counts, and constrained so it cannot become so: false requires all
    /// three count columns to be null, and true requires at least one of them to be present. Without it, a row
    /// whose counts were never filled in and a row whose provider genuinely said nothing would be the same
    /// row, and quota settlement (USAGE-004) has to tell those apart.
    /// </remarks>
    public bool UsageReported { get; set; }

    /// <summary>
    /// How the attempt ended. An enum, not a message — see the type's remarks on why this table has no
    /// free-text column.
    /// </summary>
    public AiUsageOutcome Outcome { get; set; }
}
