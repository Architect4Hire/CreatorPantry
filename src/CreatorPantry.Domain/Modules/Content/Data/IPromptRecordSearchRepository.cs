using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>
/// The purpose-built paged query behind a creator's prompt library. Persistence only — no domain decisions, no
/// caching, and never <c>IgnoreQueryFilters</c>: workspace scope comes from the global query filter, so another
/// workspace's prompts are simply not there to be filtered, counted, or paged past.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IPromptRecordRepository"/> on purpose</strong>, exactly as
/// <c>IRecipeSearchRepository</c> is separate from <c>IRecipeRepository</c>. That interface is the write seam's:
/// one <c>Add</c>, and the entity it stages. Nothing here materialises a <see cref="PromptRecord"/> — rows are
/// projected in SQL, truncated in SQL, and arrive as a record that is not the entity.
/// </para>
/// <para>
/// <strong>No cursor is minted here.</strong> A cursor is bound to the route and filters it was issued for, and
/// a repository is handed a predicate rather than a route — so this reports only whether another page follows,
/// and Business turns that into a cursor with <c>PageBuilder</c>.
/// </para>
/// </remarks>
public interface IPromptRecordSearchRepository
{
    /// <summary>
    /// Reads one page of prompt summaries, newest first.
    /// </summary>
    /// <returns>
    /// The page's rows, and whether another page follows. An empty page with no more rows is the honest answer
    /// for a workspace with no prompts and for filters nothing matches alike — neither is an error, and a search
    /// that matched nothing must not be distinguishable from a library that is empty.
    /// </returns>
    /// <remarks>
    /// Fetches one row beyond the limit and discards it, which is how "does another page follow" is answered
    /// without a second <c>COUNT</c> against a set that may have changed in between, and without ever promising
    /// a further page that turns out not to exist.
    /// </remarks>
    Task<(IReadOnlyList<PromptSummaryRecord> Rows, bool HasMore)> SearchAsync(
        PromptSearchCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// Counts every prompt matching the criteria's filters, across all pages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second method rather than a field on the page, so a caller that does not need a total does not pay for
    /// one. Both methods are built from the same private filter, which is what stops the counted set and the
    /// paged set from drifting apart — a total that counted a different set than it paged would be worse than no
    /// total at all.
    /// </para>
    /// <para>
    /// <see cref="PromptSearchCriteria.Position"/> and <see cref="PromptSearchCriteria.Limit"/> are deliberately
    /// ignored: applying the cursor would count what remains after the current page, which reads like a total
    /// and is not one.
    /// </para>
    /// </remarks>
    Task<int> CountAsync(PromptSearchCriteria criteria, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPromptRecordSearchRepository"/>
internal sealed class PromptRecordSearchRepository(CreatorPantryDbContext context) : IPromptRecordSearchRepository
{
    public async Task<(IReadOnlyList<PromptSummaryRecord> Rows, bool HasMore)> SearchAsync(
        PromptSearchCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var fetched = await Project(Ordered(Positioned(Filtered(criteria), criteria), criteria))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(criteria.Limit);
    }

    public Task<int> CountAsync(PromptSearchCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        // Filtered, and deliberately neither positioned nor limited: a count of what follows the current page is
        // not a total, and a count capped at the page size is not one either.
        return Filtered(criteria).CountAsync(cancellationToken);
    }

    /// <summary>
    /// Every filter, and nothing else — no ordering, no keyset, no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place the filters are expressed, shared by the page and the count. Two copies would be two
    /// predicates that had to stay identical, and the first time they diverged the product would report a total
    /// for a set it had not paged.
    /// </para>
    /// <para>
    /// No <c>WorkspaceId</c> predicate. The global query filter already scopes <c>PromptRecords</c>, and writing
    /// one by hand would suggest the filter is optional.
    /// </para>
    /// </remarks>
    private IQueryable<PromptRecord> Filtered(PromptSearchCriteria criteria)
    {
        var prompts = context.PromptRecords.AsNoTracking();

        if (criteria.Filters.ChannelKey is { } channelKey)
        {
            // Equality, and the leading edge of IX_PromptRecords_Workspace_Channel_Created together with the
            // workspace the query filter supplies — so this is a seek that arrives already in CreatedAt DESC
            // order, not a filter applied after one.
            prompts = prompts.Where(prompt => prompt.ChannelKey == channelKey);
        }

        if (criteria.Filters.Search is { } term)
        {
            // Lowered on both sides rather than relying on the collation, matching the recipe search: SQL
            // Server's default is case-insensitive and SQLite's is not, and a search should mean the same thing
            // in a test as in production. Forfeits an index seek, which a substring match never had — this is a
            // residual predicate over whichever seek above narrowed the set, and neither index covers these two
            // columns, so it costs a lookup per row examined. Bounded by the workspace, which is why 12.3 made
            // Text nvarchar(4000) rather than MAX.
            //
            // GeneratedText is deliberately absent: the draft column records what the creator changed, so
            // matching it would find a prompt by the words they deleted.
            prompts = prompts.Where(prompt =>
                prompt.Text.ToLower().Contains(term)
                || (prompt.Label != null && prompt.Label.ToLower().Contains(term)));
        }

        return prompts;
    }

    /// <summary>
    /// Resumes after the last row of the previous page.
    /// </summary>
    /// <remarks>
    /// This predicate and the <c>ORDER BY</c> in <see cref="Ordered"/> must name the same two columns in the
    /// same two directions. That is the whole correctness condition of a keyset: if they disagree by one column
    /// or one direction, paging silently repeats or skips rows rather than failing.
    /// </remarks>
    private static IQueryable<PromptRecord> Positioned(
        IQueryable<PromptRecord> prompts, PromptSearchCriteria criteria) =>
        criteria.Position is not { } position
            ? prompts
            : prompts.Where(prompt =>
                prompt.CreatedAt < position.CreatedAt
                || (prompt.CreatedAt == position.CreatedAt
                    && prompt.Id.CompareTo(position.PromptRecordId) < 0));

    /// <summary>
    /// Newest first, ties broken by id, fetching one row more than asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tie-break is not decoration. Two prompts saved in the same tick — which one commit of a batch of
    /// generated images would produce — would otherwise have no defined order between them, and a page boundary
    /// falling between them would repeat one and skip the other on every read.
    /// </para>
    /// <para>
    /// Both indexes 12.3 built have these columns in these directions, so this is never a sort operator:
    /// <c>IX_PromptRecords_Workspace_Created</c> unfiltered, and
    /// <c>IX_PromptRecords_Workspace_Channel_Created</c> when a channel narrows it.
    /// </para>
    /// </remarks>
    private static IQueryable<PromptRecord> Ordered(
        IQueryable<PromptRecord> prompts, PromptSearchCriteria criteria) =>
        prompts
            .OrderByDescending(prompt => prompt.CreatedAt)
            .ThenByDescending(prompt => prompt.Id)

            // One row past the limit. That row is never returned — it is how the page learns whether another
            // follows, without a second COUNT against a set that may have changed in between.
            .Take(criteria.Limit + 1);

    /// <summary>
    /// Projects summaries in SQL, truncating the prompt there rather than here.
    /// </summary>
    /// <remarks>
    /// <c>Substring</c> translates to <c>SUBSTRING</c>, so a page of a hundred rows transfers a hundred
    /// previews instead of a hundred prompts of up to 4,000 characters each. <c>Text.Length</c> becomes
    /// <c>LEN</c>, which is what lets a client tell a truncated preview from a whole short prompt without being
    /// sent the rest to find out.
    /// </remarks>
    private static IQueryable<PromptSummaryRecord> Project(IQueryable<PromptRecord> prompts) =>
        prompts.Select(prompt => new PromptSummaryRecord(
            prompt.Id,
            prompt.ChannelKey,
            prompt.ImageKind,
            prompt.Text.Substring(0, ContentPolicy.PromptPreviewMaxLength),
            prompt.Text.Length,
            prompt.Label,
            prompt.Source,
            prompt.RecipeId,
            prompt.RecipeVersionId,
            prompt.CreatedAt));
}
