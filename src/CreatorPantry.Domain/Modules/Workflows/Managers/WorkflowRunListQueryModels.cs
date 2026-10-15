using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>
/// What makes an in-progress run need a look, as data the database can match on: every reason is decided by the
/// run's own row, so a list never asks anything per run (PERF-001).
/// </summary>
/// <param name="IdleBefore">A run last touched before this has stood still too long.</param>
/// <param name="OutdatedVersions">Every shipped version older than its workflow's newest, as <see cref="VersionKey"/>.</param>
/// <param name="BlockedSteps">
/// Every step whose actions the reader cannot take, though someone could, as <see cref="StepKey"/>. Depends on the
/// reader's role, so it is built per request.
/// </param>
public sealed record WorkflowRunAttentionCriteria(
    DateTimeOffset IdleBefore,
    IReadOnlyList<string> OutdatedVersions,
    IReadOnlyList<string> BlockedSteps)
{
    /// <summary>Between a workflow's key and its version. A key is a dotted slug, so it can never hold one.</summary>
    public const string VersionSeparator = "@";

    /// <summary>Between a version and a step's key, which is a slug too.</summary>
    public const string StepSeparator = "#";

    /// <summary>
    /// <c>key@version</c>. The repository builds the same string from a run's columns with the same separators to
    /// match it, so the two formats have one definition.
    /// </summary>
    public static string VersionKey(string definitionKey, int version) =>
        string.Create(CultureInfo.InvariantCulture, $"{definitionKey}{VersionSeparator}{version}");

    /// <summary><c>key@version#step</c>, as <see cref="VersionKey"/>.</summary>
    public static string StepKey(string definitionKey, int version, string stepKey) =>
        string.Create(CultureInfo.InvariantCulture, $"{VersionKey(definitionKey, version)}{StepSeparator}{stepKey}");
}

/// <summary>Where one page of runs ended: the last run's <c>updatedAt</c> and id, the whole of the list's order.</summary>
public sealed record WorkflowRunListPosition(DateTimeOffset UpdatedAt, Guid RunId)
{
    /// <summary>False for a cursor whose parts are not a moment and an id.</summary>
    public static bool TryCreate(ReferenceCursor cursor, out WorkflowRunListPosition? position)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        position = null;

        if (!long.TryParse(cursor.SortValue, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(cursor.TieBreaker, "D", out var runId))
        {
            return false;
        }

        position = new WorkflowRunListPosition(new DateTimeOffset(ticks, TimeSpan.Zero), runId);

        return true;
    }

    /// <summary>UTC ticks, so the moment survives the cursor exactly and no zone or format can change it.</summary>
    public static string FormatSortValue(DateTimeOffset updatedAt) =>
        updatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One read of the list: which runs, for whom, from where, and how many.</summary>
/// <param name="MembershipId">The reader, which "mine" is.</param>
/// <param name="Scope">What a cursor issued for this read is bound to.</param>
public sealed record WorkflowRunListCriteria(
    WorkflowRunListView View,
    string? DefinitionKey,
    Guid MembershipId,
    WorkflowRunAttentionCriteria Attention,
    string Scope,
    WorkflowRunListPosition? Position = null,
    int? RequestedLimit = null)
{
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);

    /// <summary>Oldest first for the runs that need a look — the longest-waiting at the top — and newest first otherwise.</summary>
    public bool Ascending => View == WorkflowRunListView.Attention;
}

/// <summary>
/// Turns a validated <see cref="WorkflowRunListViewModel"/> into <see cref="WorkflowRunListCriteria"/>, or refuses a
/// cursor that was issued for another list.
/// </summary>
public static class WorkflowRunListQueryFactory
{
    public static bool TryCreate(
        WorkflowRunListViewModel model,
        Guid workspaceId,
        Guid membershipId,
        WorkspaceRole role,
        WorkflowRunAttentionCriteria attention,
        out WorkflowRunListCriteria? criteria)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(attention);

        criteria = null;

        var view = WorkflowRunListInputs.Normalize(model.View) is { } named && WorkflowRunListInputs.TryParseView(named, out var parsed)
            ? parsed
            : WorkflowRunListView.Mine;
        var definition = WorkflowRunListInputs.Normalize(model.Definition);
        var scope = ScopeFor(workspaceId, membershipId, role, view, definition);

        if (!ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor))
        {
            return false;
        }

        WorkflowRunListPosition? position = null;
        if (cursor is not null && !WorkflowRunListPosition.TryCreate(cursor, out position))
        {
            return false;
        }

        criteria = new WorkflowRunListCriteria(view, definition, membershipId, attention, scope, position, model.Limit);

        return true;
    }

    /// <summary>
    /// The workspace, the reader, the reader's role and every filter. "Mine" is the reader's and what needs a look
    /// depends on their role, so a cursor handed to someone else — or replayed after a role change, or against
    /// another view — is refused rather than answered with a plausible page from the wrong list.
    /// </summary>
    private static string ScopeFor(Guid workspaceId, Guid membershipId, WorkspaceRole role, WorkflowRunListView view, string? definition) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"workflow-runs|{workspaceId:D}|{membershipId:D}|role={(int)role}|view={(int)view}|definition={definition}");
}
