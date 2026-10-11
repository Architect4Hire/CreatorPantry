using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The application boundary for "what has this workspace already made for this themed day".
/// </summary>
public interface IThemedDayHistoryFacade
{
    /// <summary>
    /// One page of a theme's history in the resolved workspace, newest outcome first.
    /// </summary>
    /// <param name="themeKey">
    /// The theme's key, from the route. Never a workspace: an unknown key, a deleted theme's and another
    /// workspace's are one <see cref="ContentErrorCodes.WeeklyThemeNotFound"/>.
    /// </param>
    /// <param name="model">The cursor and page size. Carries no workspace and no theme.</param>
    /// <remarks>
    /// <para>
    /// <strong>No role gate.</strong> <c>Viewer</c> is the lowest role, so a resolved workspace context already
    /// is the authorization, as for the week this history belongs to.
    /// </para>
    /// <para>
    /// <strong>Cached, and the first workspace-private read that is.</strong> The whole bounded history is one
    /// entry and a page is a slice of it, so paging costs nothing after the first read. See
    /// <see cref="IThemedDayHistoryCache"/> for how every key carries the workspace and how a write retires it.
    /// </para>
    /// </remarks>
    Task<OperationResult<CursorPageServiceModel<ThemedDayHistoryItemServiceModel>>> GetPageAsync(
        string themeKey, ThemedDayHistoryViewModel model, CancellationToken cancellationToken);
}

/// <summary>
/// What a write calls once it has committed something that changes a themed day's history.
/// </summary>
/// <remarks>
/// <para>
/// A facade of its own, and one that depends on the cache and nothing else, so any facade in any module can
/// take it without resolving the read's dependencies or closing a dependency cycle through them.
/// </para>
/// <para>
/// <strong>Called after the write commits, and only when it did.</strong> The writes that change the answer
/// are: a piece of work created or patched (its theme, title or archived state), a reference added to or
/// removed from one, a post drafted, edited, accepted, rejected or reaffirmed, a recipe change marking
/// accepted posts for review, and a theme deleted. Accepting a recipe draft is not on the list because it
/// cannot change the answer by itself: the recipe does not exist until then, so the reference that makes it
/// history is always added afterwards — and adding a reference is on the list.
/// </para>
/// <para>
/// <strong>What a missed call costs.</strong> The cache fails open, so an invalidation the store refused is
/// logged and otherwise silent. The history is then out of date for at most
/// <c>ThemedDayHistoryPolicy.CacheLifetime</c> — including a deleted theme's, which answers from its entry
/// rather than 404 until that entry expires. Bounded, and never another workspace's data.
/// </para>
/// </remarks>
public interface IThemedDayHistoryInvalidationFacade
{
    /// <summary>Retires every cached themed-day history of the resolved workspace.</summary>
    Task InvalidateAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IThemedDayHistoryInvalidationFacade"/>
internal sealed class ThemedDayHistoryInvalidationFacade(IThemedDayHistoryCache cache)
    : IThemedDayHistoryInvalidationFacade
{
    public Task InvalidateAsync(CancellationToken cancellationToken) => cache.InvalidateAsync(cancellationToken);
}

/// <inheritdoc cref="IThemedDayHistoryFacade"/>
internal sealed class ThemedDayHistoryFacade(
    IValidator<ThemedDayHistoryViewModel> validator,
    IThemedDayHistoryBusiness business,
    IThemedDayHistoryCache cache,
    IWorkspaceContext workspace) : IThemedDayHistoryFacade
{
    public async Task<OperationResult<CursorPageServiceModel<ThemedDayHistoryItemServiceModel>>> GetPageAsync(
        string themeKey, ThemedDayHistoryViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // A key that could never have been issued is answered like one that does not exist, before it can be
        // made part of a cache key or a cursor scope.
        var key = WeeklyThemeInputChecks.Normalize(themeKey);

        if (key is null || !WeeklyThemeInputChecks.IsKey(key))
        {
            return Failure(new OperationError(
                ContentErrorCodes.WeeklyThemeNotFound,
                "This workspace has no weekly theme with that key.",
                new Dictionary<string, string[]>()));
        }

        var validation = await validator.ValidateAsync(model, cancellationToken);

        // The workspace comes from the resolved context, never from the model — which has no field for it. It
        // and the theme are bound into the cursor's scope, so a cursor cannot be replayed across either.
        var scope = ThemedDayHistoryPosition.ScopeFor(workspace.WorkspaceId, key);

        ThemedDayHistoryPosition? position = null;

        if (!validation.IsValid
            || !ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor)
            || (cursor is not null && !ThemedDayHistoryPosition.TryCreate(cursor, out position)))
        {
            return Failure(OperationError.Validation(
                ContentErrorCodes.ThemedDayHistoryCursorInvalid,
                "This cursor was not issued for this theme's history. Start again without one.",
                [("cursor", "Start the history again without a cursor.")]));
        }

        var (history, generation) = await cache.GetAsync(key, cancellationToken);

        if (history is null)
        {
            var derived = await business.DeriveAsync(key, cancellationToken);

            if (!derived.Succeeded)
            {
                // Never cached: a theme that does not exist today may be written tomorrow.
                return Failure(derived.Error!);
            }

            history = derived.Value!;

            // Under the generation read before deriving. If a write invalidated in between, this lands under a
            // stamp nothing reads any more, rather than under the new one with the old answer.
            await cache.SetAsync(key, generation, history, cancellationToken);
        }

        var limit = ThemedDayHistoryPolicy.ClampPageSize(model.Limit);

        IReadOnlyList<ThemedDayHistoryItemServiceModel> remaining = position is null
            ? history.Items
            : [.. history.Items.Where(position.Precedes)];

        var page = remaining.Take(limit).ToList();

        // No more rows means no cursor, so a client loops until nextCursor is null.
        var nextCursor = remaining.Count > limit ? ThemedDayHistoryPosition.Encode(page[^1], scope) : null;

        return OperationResult<CursorPageServiceModel<ThemedDayHistoryItemServiceModel>>.Success(
            new CursorPageServiceModel<ThemedDayHistoryItemServiceModel>(page, nextCursor));
    }

    private static OperationResult<CursorPageServiceModel<ThemedDayHistoryItemServiceModel>> Failure(
        OperationError error) =>
        OperationResult<CursorPageServiceModel<ThemedDayHistoryItemServiceModel>>.Failure(error);
}
