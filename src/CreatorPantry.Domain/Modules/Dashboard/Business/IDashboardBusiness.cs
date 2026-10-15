using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Dashboard.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Dashboard.Business;

/// <summary>Puts the workspace's dashboard together from what each module reports (13.11).</summary>
public interface IDashboardBusiness
{
    Task<DashboardServiceModel> GetAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDashboardBusiness"/>
/// <remarks>
/// <para>
/// <strong>A projection, and only that.</strong> Every number and every row is asked of the module that owns
/// it, through that module's facade, and passed on: nothing is counted here, stored here or remembered between
/// requests. The dashboard owns no table, which is why this module has no data layer.
/// </para>
/// <para>
/// <strong>Bounded whatever the workspace holds.</strong> Each list is capped at
/// <see cref="DashboardPolicy.ListLimit"/> by the module that reads it and each count is one grouped query, so
/// the work a request does is a fixed number of statements. Readiness is the one evaluation done per recipe,
/// and it is done only for the recipes already listed.
/// </para>
/// <para>
/// <strong>One section failing is that section's business.</strong> Each is read on its own and a failure
/// marks it <see cref="DashboardSectionStatus.Unavailable"/>, with the type of the failure logged and nothing
/// else; the others are still answered. The calls are made one after another, because they share the request's
/// one unit of work.
/// </para>
/// <para>
/// <strong>"Today" is the workspace's.</strong> Upcoming content is counted from today in the brand profile's
/// zone, and a planned date is a calendar day in that zone; a scheduled post's instant is compared with the
/// start of the window's first and last day there. <strong>With no zone set there is no today</strong>: the
/// answer carries a null day and marks the one section that depends on a day unavailable, rather than
/// answering in UTC as though it knew. Everything that does not turn on a date still answers.
/// </para>
/// </remarks>
internal sealed class DashboardBusiness(
    IRecipeDashboardFacade recipes,
    IRecipeReadinessFacade readiness,
    IRecipeFacade recipeTitles,
    IAiDashboardFacade proposals,
    IContentDashboardFacade board,
    IMediaDashboardFacade library,
    IPublishingDashboardFacade publishing,
    IBrandProfileFacade brand,
    ITimeZoneConverter zones,
    IClock clock,
    ILogger<DashboardBusiness> logger) : IDashboardBusiness
{
    public async Task<DashboardServiceModel> GetAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var zone = await TimeZoneAsync(cancellationToken);
        var today = zone is null ? (DateOnly?)null : zones.LocalDate(now, zone);

        // The two facades that take a window still need one. With no zone the parts that depend on it are
        // not published, so this stands in for a day the answer never claims to know; the counts and the
        // lists beside them are the same whatever window is passed.
        var from = today ?? DateOnly.FromDateTime(now.UtcDateTime);
        var through = from.AddDays(DashboardPolicy.UpcomingDays - 1);
        var windowEnd = StartOf(through.AddDays(1), zone);

        var work = await SectionAsync("recipes", () => recipes.GetWorkAsync(cancellationToken), cancellationToken);
        var blockers = work.Data is { } inProgress
            ? await SectionAsync("readiness", () => ReadinessAsync(inProgress, cancellationToken), cancellationToken)
            : DashboardSection<DashboardReadinessServiceModel>.Unavailable();

        var awaiting = await SectionAsync("proposals", () => ProposalsAsync(cancellationToken), cancellationToken);
        var followUps = await SectionAsync("test follow-ups", () => recipes.GetTestFollowUpsAsync(cancellationToken), cancellationToken);
        var boardSummary = await SectionAsync(
            "board", () => board.GetBoardAsync(from, through, DashboardPolicy.ListLimit, cancellationToken), cancellationToken);
        var uploads = await SectionAsync(
            "uploads",
            async () => new DashboardRecentUploadsServiceModel(await library.ListRecentAsync(DashboardPolicy.ListLimit, cancellationToken)),
            cancellationToken);
        var ledger = await SectionAsync(
            "publishing", () => publishing.GetSummaryAsync(now, windowEnd, DashboardPolicy.ListLimit, cancellationToken), cancellationToken);

        // The cards the publishing rows were published from, named once for both places they appear. Without
        // their titles the rows still stand: a post is still listed, by its network and its time.
        var titles = ledger.Data is { } summary
            ? await CardTitlesAsync([.. summary.NeedsAttention, .. summary.Upcoming], cancellationToken)
            : new Dictionary<Guid, string>();

        return new DashboardServiceModel(
            now,
            zone,
            today,
            work,
            blockers,
            awaiting,
            followUps,
            // The one section that is a claim about dates. Without a zone it has no honest answer, and
            // "nothing upcoming" would be the most misleading of the wrong ones.
            today is { } day
                ? Upcoming(boardSummary, ledger, titles, day, through)
                : DashboardSection<DashboardUpcomingServiceModel>.Unavailable(),
            boardSummary.Data is { } columns
                ? DashboardSection<DashboardBoardServiceModel>.Ready(
                    new DashboardBoardServiceModel(columns.Columns.Sum(column => column.Count), columns.Columns))
                : DashboardSection<DashboardBoardServiceModel>.Unavailable(),
            uploads,
            ledger.Data is { } state
                ? DashboardSection<DashboardPublishingServiceModel>.Ready(new DashboardPublishingServiceModel(
                    state.Counts, state.NeedsAttentionCount, [.. state.NeedsAttention.Select(item => Named(item, titles))]))
                : DashboardSection<DashboardPublishingServiceModel>.Unavailable());
    }

    /// <summary>
    /// Upcoming needs both sources. Either one missing makes the section unavailable rather than half of it:
    /// "nothing upcoming" would be false if the half that failed had something.
    /// </summary>
    private static DashboardSection<DashboardUpcomingServiceModel> Upcoming(
        DashboardSection<ContentBoardSummaryServiceModel> board,
        DashboardSection<PublishingSummaryServiceModel> ledger,
        IReadOnlyDictionary<Guid, string> titles,
        DateOnly today,
        DateOnly through) =>
        board.Data is { } cards && ledger.Data is { } posts
            ? DashboardSection<DashboardUpcomingServiceModel>.Ready(new DashboardUpcomingServiceModel(
                today,
                through,
                cards.UpcomingCount,
                cards.Upcoming,
                posts.UpcomingCount,
                [.. posts.Upcoming.Select(item => Named(item, titles))]))
            : DashboardSection<DashboardUpcomingServiceModel>.Unavailable();

    /// <summary>The real readiness evaluation, for the in-progress recipes the dashboard already lists and no others.</summary>
    private async Task<DashboardReadinessServiceModel> ReadinessAsync(
        RecipeWorkSummaryServiceModel work, CancellationToken cancellationToken)
    {
        var blocked = new List<DashboardBlockedRecipeServiceModel>();
        var unevaluated = 0;

        foreach (var recipe in work.Recent)
        {
            var evaluated = await readiness.EvaluateAsync(recipe.Id, cancellationToken);
            if (!evaluated.Succeeded)
            {
                // Gone since it was listed, or not evaluable just now. Not listed as blocked, and not as clear.
                unevaluated++;
                continue;
            }

            var result = evaluated.Value!;
            if (result.HasBlockers)
            {
                blocked.Add(new DashboardBlockedRecipeServiceModel(
                    recipe.Id,
                    recipe.Title,
                    result.BlockerCount,
                    [.. result.Findings
                        .Where(finding => finding.Status is RecipeReadinessStatus.Blocker)
                        .Take(DashboardPolicy.BlockersPerRecipe)
                        .Select(finding => new DashboardBlockerServiceModel(finding.RuleId, finding.Summary))]));
            }
        }

        // Evaluated means checked: a recipe whose check failed is counted as unevaluated, never as clear.
        return new DashboardReadinessServiceModel(work.Recent.Count - unevaluated, unevaluated, blocked);
    }

    private async Task<DashboardAiProposalsServiceModel> ProposalsAsync(CancellationToken cancellationToken)
    {
        var summary = await proposals.GetAwaitingDecisionAsync(DashboardPolicy.ListLimit, cancellationToken);

        var recipeIds = summary.Recent.Select(item => item.RecipeId).OfType<Guid>().Distinct().ToList();
        var named = recipeIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await recipeTitles.ListTitlesAsync(recipeIds, cancellationToken)).ToDictionary(recipe => recipe.Id, recipe => recipe.Title);

        return new DashboardAiProposalsServiceModel(
            summary.AwaitingDecision,
            [.. summary.Recent.Select(item => new DashboardAiProposalServiceModel(
                item.OperationId,
                item.TaskType,
                item.RecipeId,
                item.RecipeId is { } id && named.TryGetValue(id, out var title) ? title : null,
                item.ProposedAt))]);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> CardTitlesAsync(
        IEnumerable<PublishingDashboardItemServiceModel> items, CancellationToken cancellationToken)
    {
        var ids = items.Select(item => item.ContentCardId).OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        try
        {
            return (await board.ListTitlesAsync(ids, cancellationToken)).ToDictionary(card => card.Id, card => card.Title);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Dashboard card titles could not be read ({ExceptionType}).", exception.GetType().Name);

            return new Dictionary<Guid, string>();
        }
    }

    private static DashboardPublishingItemServiceModel Named(
        PublishingDashboardItemServiceModel item, IReadOnlyDictionary<Guid, string> titles) =>
        new(
            item.Id,
            item.ContentCardId,
            item.ContentCardId is { } id && titles.TryGetValue(id, out var title) ? title : null,
            item.Platform,
            item.Status,
            item.ScheduledAt);

    /// <summary>The brand profile's zone, when there is a profile and it has one this server knows.</summary>
    private async Task<string?> TimeZoneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var profile = await brand.GetAsync(cancellationToken);
            var zone = profile.Succeeded ? profile.Value!.TimeZoneId : null;

            return zone is not null && zones.IsValidZone(zone) ? zone : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Without it the dashboard still answers, minus the one section that needs a day, and says so.
            logger.LogWarning("Dashboard time zone could not be read ({ExceptionType}).", exception.GetType().Name);

            return null;
        }
    }

    /// <summary>The instant a calendar day starts in the zone, or at UTC midnight with none.</summary>
    private DateTimeOffset StartOf(DateOnly day, string? zone) =>
        zone is null
            ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : zones.ToUtc(day, TimeOnly.MinValue, zone).Utc;

    /// <summary>One section, read on its own. A failure is that section's, recorded by type alone.</summary>
    private async Task<DashboardSection<T>> SectionAsync<T>(
        string name, Func<Task<T>> read, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return DashboardSection<T>.Ready(await read());
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Dashboard section {Section} could not be read ({ExceptionType}).", name, exception.GetType().Name);

            return DashboardSection<T>.Unavailable();
        }
    }
}
