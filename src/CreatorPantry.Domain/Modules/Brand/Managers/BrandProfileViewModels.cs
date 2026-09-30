using CreatorPantry.Domain.Managers.Patching;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>One default channel. Carries an opaque key; channel constraints live in channel profiles.</summary>
public sealed record BrandChannelDefaultInput
{
    public string? ChannelKey { get; init; }
}

/// <summary>One website or reference link.</summary>
public sealed record BrandLinkInput
{
    public BrandLinkKind? Kind { get; init; }

    public string? Url { get; init; }

    public string? Label { get; init; }
}

/// <summary>One linked logo. Accepted by the contract but refused until the media seam can verify ownership.</summary>
public sealed record BrandAssetInput
{
    public Guid? MediaAssetId { get; init; }

    public BrandAssetRole? Role { get; init; }
}

/// <summary>
/// Creates the workspace's brand profile. Carries no workspace, owner, revision, audit or style field: the
/// workspace comes from the route and membership, and voice, tone and visual direction belong to the brand
/// style guide.
/// </summary>
public sealed record CreateBrandProfileViewModel
{
    public string? BrandName { get; init; }

    public string? ShortDescription { get; init; }

    public string? DefaultAudience { get; init; }

    public string? Locale { get; init; }

    public string? TimeZoneId { get; init; }

    public IReadOnlyList<BrandChannelDefaultInput?>? ChannelDefaults { get; init; }

    public IReadOnlyList<BrandLinkInput?>? Links { get; init; }

    public IReadOnlyList<BrandAssetInput?>? Assets { get; init; }
}

/// <summary>
/// A JSON Merge Patch over the brand profile: a field not mentioned is left alone, a field sent with a value is
/// set, and a field sent as <c>null</c> is cleared. The three lists replace as a whole.
/// </summary>
/// <remarks>
/// <c>ExpectedConcurrencyToken</c> and <c>Reason</c> are not profile fields: the first is the
/// <c>concurrencyToken</c> of the read this edit was composed against, the second is recorded on the revision
/// the edit writes.
/// </remarks>
public sealed record UpdateBrandProfileViewModel
{
    public string? ExpectedConcurrencyToken { get; init; }

    public string? Reason { get; init; }

    public PatchField<string?> BrandName { get; init; }

    public PatchField<string?> ShortDescription { get; init; }

    public PatchField<string?> DefaultAudience { get; init; }

    public PatchField<string?> Locale { get; init; }

    public PatchField<string?> TimeZoneId { get; init; }

    public PatchField<IReadOnlyList<BrandChannelDefaultInput?>?> ChannelDefaults { get; init; }

    public PatchField<IReadOnlyList<BrandLinkInput?>?> Links { get; init; }

    public PatchField<IReadOnlyList<BrandAssetInput?>?> Assets { get; init; }
}
