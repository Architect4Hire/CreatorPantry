using System.ComponentModel;
using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>The query string of a DAM detail read (DAM-003).</summary>
/// <remarks>
/// One parameter, and it exists for one reason: <see cref="IncludeDeleted"/> is how a creator asks to see an
/// asset they deleted. Ordinary reads answer 404 for a tombstone, which is what 12.9e's "exclusion from ordinary
/// reads" requires, so being shown one has to be asked for rather than defaulted into — a panel that forgot to
/// check would otherwise render a deleted asset as live.
/// </remarks>
/// <param name="IncludeDeleted">
/// True to return a soft-deleted asset with its tombstone instead of 404. The bytes stay unreachable either way.
/// </param>
public sealed record MediaAssetDetailViewModel(
    [property: FromQuery(Name = "includeDeleted")]
    [property: Description("True to return a soft-deleted asset with its deletedAt and deletedByMembershipId instead of 404. Defaults to false. The image and download routes refuse a deleted asset regardless of this flag.")]
    bool? IncludeDeleted = null);

/// <summary>The query string of one asset's utilization history.</summary>
/// <remarks>
/// No filters, deliberately: this history is read whole, newest first. A creator narrowing their own usage log by
/// platform or date is a filtered read worth having once there is enough of it to narrow, and adding parameters
/// later is compatible (api-contract.md).
/// </remarks>
public sealed record MediaAssetUtilizationViewModel(
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace and the asset it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null,
    [property: FromQuery(Name = "includeTotal")]
    [property: Description("Whether to count every logged use alongside the page. Defaults to true; pass false when following a cursor, since counting costs a second query per page.")]
    bool? IncludeTotal = null);

/// <summary>Shape checks on a utilization history read.</summary>
/// <remarks>
/// Only the cursor can be malformed. The page size is clamped rather than refused, following every other paged
/// route here, so a client cannot fail a read by asking for too much.
/// </remarks>
public sealed class MediaAssetUtilizationViewModelValidator : AbstractValidator<MediaAssetUtilizationViewModel>
{
    public MediaAssetUtilizationViewModelValidator()
    {
        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");
    }
}
