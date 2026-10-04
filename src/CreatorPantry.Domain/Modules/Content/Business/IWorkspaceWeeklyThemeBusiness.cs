using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

public interface IWorkspaceWeeklyThemeBusiness
{
    /// <summary>The workspace's themes, live week first. Always succeeds; an empty week is a real state.</summary>
    Task<OperationResult<WeeklyThemeWeekServiceModel>> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The theme with this exact key, retired or not, or <see cref="ContentErrorCodes.WeeklyThemeNotFound"/>.
    /// What a consumer that stores a theme key validates against — and the reason an unknown key, another
    /// workspace's key, and a deleted key are one answer rather than three.
    /// </summary>
    Task<OperationResult<WeeklyThemeServiceModel>> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the whole week. A replace that asks for the week the workspace already has writes nothing.
    /// </summary>
    Task<OperationResult<WeeklyThemeWeekServiceModel>> ReplaceAsync(
        string actorUserId, ReplaceWeeklyThemesViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one theme outright, by key. Unlike retirement this does not keep the key resolvable, which is why
    /// it sits behind a higher bar.
    /// </summary>
    Task<OperationResult<WeeklyThemeWeekServiceModel>> DeleteAsync(
        string actorUserId, string key, CancellationToken cancellationToken);
}

internal sealed class WorkspaceWeeklyThemeBusiness(
    IWorkspaceWeeklyThemeDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : IWorkspaceWeeklyThemeBusiness
{
    public async Task<OperationResult<WeeklyThemeWeekServiceModel>> GetAsync(CancellationToken cancellationToken) =>
        OperationResult<WeeklyThemeWeekServiceModel>.Success(Week(await dataLayer.ListAsync(cancellationToken)));

    public async Task<OperationResult<WeeklyThemeServiceModel>> FindAsync(
        string key, CancellationToken cancellationToken)
    {
        var normalized = WeeklyThemeInputChecks.Normalize(key);

        // A key that could never have been issued is answered like one that simply does not exist. There is
        // nothing to look up, and saying so differently would be a shape oracle for no benefit.
        if (normalized is null || !WeeklyThemeInputChecks.IsKey(normalized))
        {
            return OperationResult<WeeklyThemeServiceModel>.Failure(NotFound());
        }

        var theme = await dataLayer.FindAsync(normalized, cancellationToken);

        return theme is null
            ? OperationResult<WeeklyThemeServiceModel>.Failure(NotFound())
            : OperationResult<WeeklyThemeServiceModel>.Success(ToServiceModel(theme));
    }

    public async Task<OperationResult<WeeklyThemeWeekServiceModel>> ReplaceAsync(
        string actorUserId, ReplaceWeeklyThemesViewModel model, CancellationToken cancellationToken)
    {
        // The backstop for any caller that reaches Business without the facade's validator — a worker, a plugin.
        // The facade has already refused the same input; this is why the rules live in one place.
        var failures = WeeklyThemeInputChecks.Themes(model.Themes).ToList();
        if (failures.Count > 0)
        {
            return OperationResult<WeeklyThemeWeekServiceModel>.Failure(
                OperationError.Validation(ContentErrorCodes.WeeklyThemesInvalid, CannotSave, failures));
        }

        var stored = await dataLayer.ListAsync(cancellationToken);
        var byKey = stored.ToDictionary(theme => theme.Key, StringComparer.Ordinal);
        var submitted = model.Themes ?? [];

        var changes = new List<WeeklyThemeChange>();
        var submittedKeys = new HashSet<string>(StringComparer.Ordinal);
        var additions = 0;

        foreach (var input in submitted)
        {
            var key = WeeklyThemeInputChecks.Normalize(input!.Key)!;
            var day = input.Day!.Value;
            var displayName = WeeklyThemeInputChecks.Normalize(input.DisplayName)!;
            var description = WeeklyThemeInputChecks.Normalize(input.Description);
            submittedKeys.Add(key);

            if (byKey.TryGetValue(key, out var existing))
            {
                // Already exactly this, and live: nothing to write. Both the row and the token the creator holds
                // stay as they are, and the revision counter does not move for a save that said nothing new.
                if (existing.RetiredAt is null
                    && existing.Day == day
                    && string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal)
                    && string.Equals(existing.Description, description, StringComparison.Ordinal))
                {
                    continue;
                }
            }
            else
            {
                additions++;
            }

            changes.Add(new WeeklyThemeChange(key, day, displayName, description, Retire: false));
        }

        if (stored.Count + additions > WeeklyThemePolicy.MaxThemes)
        {
            return OperationResult<WeeklyThemeWeekServiceModel>.Failure(new OperationError(
                ContentErrorCodes.WeeklyThemesLimit,
                $"This workspace is holding the most weekly themes it can ({WeeklyThemePolicy.MaxThemes}), "
                    + "counting retired ones. Delete a retired theme to make room.",
                new Dictionary<string, string[]>()));
        }

        // Every live theme the creator did not submit leaves the week. Retired rather than deleted, so a record
        // that already stored its key keeps resolving — the one rule the whole shape of this entity exists for.
        foreach (var theme in stored.Where(theme => theme.RetiredAt is null && !submittedKeys.Contains(theme.Key)))
        {
            changes.Add(new WeeklyThemeChange(
                theme.Key, theme.Day, theme.DisplayName, theme.Description, Retire: true));
        }

        if (changes.Count == 0)
        {
            return OperationResult<WeeklyThemeWeekServiceModel>.Success(Week(stored));
        }

        var saved = await dataLayer.ReplaceAsync(
            new WeeklyThemeWeekPlan(clock.UtcNow, changes),
            Audit(
                actorUserId,
                ContentAuditActions.WeeklyThemesReplaced,
                ContentAuditActions.WeeklyThemesResourceType,
                workspace.WorkspaceId.ToString("D"),
                "Replaced the workspace's weekly themes.",
                before: stored.Count(theme => theme.RetiredAt is null),
                after: submitted.Count),
            cancellationToken);

        return saved ? await GetAsync(cancellationToken) : Conflict();
    }

    public async Task<OperationResult<WeeklyThemeWeekServiceModel>> DeleteAsync(
        string actorUserId, string key, CancellationToken cancellationToken)
    {
        var normalized = WeeklyThemeInputChecks.Normalize(key);
        if (normalized is null || !WeeklyThemeInputChecks.IsKey(normalized))
        {
            return OperationResult<WeeklyThemeWeekServiceModel>.Failure(NotFound());
        }

        // Tracked, unlike the read above: this is the row the layer below is about to delete.
        var theme = await dataLayer.FindForUpdateAsync(normalized, cancellationToken);
        if (theme is null)
        {
            return OperationResult<WeeklyThemeWeekServiceModel>.Failure(NotFound());
        }

        var deleted = await dataLayer.DeleteAsync(
            theme,
            Audit(
                actorUserId,
                ContentAuditActions.WeeklyThemeDeleted,
                ContentAuditActions.WeeklyThemeResourceType,
                theme.Id.ToString("D"),
                "Deleted a weekly theme.",
                before: theme.Revision,
                after: null),
            cancellationToken);

        if (!deleted)
        {
            return OperationResult<WeeklyThemeWeekServiceModel>.Failure(NotFound());
        }

        return await GetAsync(cancellationToken);
    }

    /// <summary>
    /// The live week in a creator's reading order, then the retired themes. Ordered here rather than in SQL: the
    /// set is capped at <c>WeeklyThemePolicy.MaxThemes</c> rows, and a Monday-first week is an expression no
    /// index would help with anyway.
    /// </summary>
    private static WeeklyThemeWeekServiceModel Week(IReadOnlyList<WorkspaceWeeklyTheme> themes) => new(
    [
        .. themes
            .OrderBy(theme => theme.RetiredAt is null ? 0 : 1)
            .ThenBy(theme => WeeklyThemePolicy.WeekOrder(theme.Day))
            .ThenBy(theme => theme.Key, StringComparer.Ordinal)
            .Select(ToServiceModel),
    ]);

    private static WeeklyThemeServiceModel ToServiceModel(WorkspaceWeeklyTheme theme) => new(
        theme.Day,
        theme.Key,
        theme.DisplayName,
        theme.Description,
        theme.RetiredAt,
        theme.Revision,
        theme.CreatedAt,
        theme.UpdatedAt);

    // State references only: AuditLog requires these to stay safe to display, so counts, revisions and ids —
    // never a theme's key or the creator's name for it.
    private static AuditEntry Audit(
        string actorUserId,
        string action,
        string resourceType,
        string resourceId,
        string summary,
        int? before,
        int? after) => new(
            actorUserId,
            action,
            resourceType,
            resourceId,
            CorrelationId(),
            summary,
            BeforeReference: before?.ToString(),
            AfterReference: after?.ToString());

    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    private const string CannotSave = "The weekly themes could not be saved.";

    private static OperationError NotFound() => new(
        ContentErrorCodes.WeeklyThemeNotFound,
        "This workspace has no weekly theme with that key.",
        new Dictionary<string, string[]>());

    private static OperationResult<WeeklyThemeWeekServiceModel> Conflict() =>
        OperationResult<WeeklyThemeWeekServiceModel>.Failure(new OperationError(
            ContentErrorCodes.WeeklyThemesConflict,
            "The weekly themes changed while this was saving. Reload them and apply your change again.",
            new Dictionary<string, string[]>()));
}
