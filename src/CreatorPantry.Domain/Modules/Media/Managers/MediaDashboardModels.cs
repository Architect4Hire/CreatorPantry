namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// One picture recently added to the library, as the dashboard names it: what it is called, what kind, when.
/// No bytes, no address and no file details — the picture is opened, authorised, on its own page.
/// </summary>
public sealed record RecentMediaUploadServiceModel(Guid Id, string Title, MediaAssetKind Kind, DateTimeOffset CreatedAt);
