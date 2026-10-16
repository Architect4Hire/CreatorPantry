using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>A routine due for its occurrences, by id and workspace only.</summary>
public sealed record WorkTaskTemplateClaim(Guid TemplateId, Guid WorkspaceId);

/// <summary>
/// Leases routines due for their occurrences, across workspaces (13A.30). The tenancy rules' queue-claim
/// carve-out, and listed by path in <c>BulkOperationBoundaryTests.Exemptions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identifiers only.</strong> The worker looks for work before it knows which workspace it will serve, so
/// there is no context to filter by — which is why it reads nothing here but each routine's id and workspace,
/// never a title or a date. The worker then resolves that workspace through the ordinary tenancy path and reads
/// the routine through the workspace filter; finding a row here is not authorization.
/// </para>
/// <para>
/// <strong>A generous cut-off.</strong> Whether a routine is due depends on "today" in its own zone, which SQL
/// cannot work out per row. Every zone's today is within a day of UTC's, so a next date up to the lookahead plus a
/// day past UTC's today is claimed, and the exact horizon is judged once the routine is read in its zone.
/// </para>
/// </remarks>
internal sealed class WorkTaskTemplateClaimRepository(CreatorPantryDbContext context)
{
    public async Task<IReadOnlyList<WorkTaskTemplateClaim>> ClaimDueAsync(
        Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(WorkTaskRecurrence.LookaheadDays + 1);
        var expiresAt = (DateTimeOffset?)(now + WorkTaskTemplatePolicy.LeaseDuration);

        var candidates = await context.WorkTaskTemplates
            .IgnoreQueryFilters()
            .Where(template => template.Status == WorkTaskTemplateStatus.Active
                && (template.NextOccurrenceDate == null || template.NextOccurrenceDate <= cutoff)
                && (template.LeasedBy == null || template.LeaseExpiresAt <= now))
            .OrderBy(template => template.NextOccurrenceDate)
            .Take(WorkTaskTemplatePolicy.ClaimBatchSize)
            .Select(template => new { template.Id, template.WorkspaceId })
            .ToListAsync(cancellationToken);

        var claimed = new List<WorkTaskTemplateClaim>(candidates.Count);

        foreach (var candidate in candidates)
        {
            // The guard is the claim: a routine another pass took between the scan and here no longer matches,
            // so nothing is updated and this pass moves on.
            var won = await context.WorkTaskTemplates
                .IgnoreQueryFilters()
                .Where(template => template.Id == candidate.Id
                    && template.Status == WorkTaskTemplateStatus.Active
                    && (template.LeasedBy == null || template.LeaseExpiresAt <= now))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(template => template.LeasedBy, (Guid?)leaseToken)
                        .SetProperty(template => template.LeaseExpiresAt, expiresAt),
                    cancellationToken);

            if (won == 1)
            {
                claimed.Add(new WorkTaskTemplateClaim(candidate.Id, candidate.WorkspaceId));
            }
        }

        return claimed;
    }
}
