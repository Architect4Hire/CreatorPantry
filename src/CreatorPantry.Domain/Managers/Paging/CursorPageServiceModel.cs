namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>
/// One page of a cursor-paginated collection: the items, and where to resume.
/// </summary>
/// <remarks>
/// Matches the <c>CursorPage</c> component already published in the reviewed OpenAPI document, which has
/// described this envelope since before anything implemented it. <see cref="NextCursor"/> is <c>null</c> on
/// the last page, so a client loops until it is null rather than comparing counts against a page size.
/// </remarks>
public sealed record CursorPageServiceModel<T>(IReadOnlyList<T> Items, string? NextCursor);
