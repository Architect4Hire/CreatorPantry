using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The comparison query: which two of this guide's versions to compare. Bound from the query string.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No workspace parameter and no guide parameter.</strong> Both are route segments resolved
/// server-side before the action runs. A field here for either would be one a request could set (tenancy.md).
/// </para>
/// <para>
/// <strong>Version numbers rather than version ids, which is a safety decision rather than a taste.</strong>
/// <c>UX_BrandStyleGuideVersions_Workspace_Guide_VersionNumber</c> makes a number unique within one guide and
/// nothing else, so the resolving query is <c>BrandStyleGuideId == route id AND VersionNumber == n</c> and a
/// number naming another guide's version is not merely refused but unrepresentable. Accepting ids would make
/// "does this version belong to the guide in the route" a check someone has to remember to write. Numbers are
/// also what the history lists and what a creator cites.
/// </para>
/// <para>
/// <strong>Nullable, so a missing parameter is a refusal rather than a zero.</strong> A non-nullable
/// <c>int</c> would bind an absent <c>from</c> to <c>0</c> and reach the validator as an out-of-range number,
/// which is a different complaint from the true one.
/// </para>
/// <para>
/// Names are given explicitly in lowercase and both carry a <see cref="DescriptionAttribute"/>: the generated
/// OpenAPI document otherwise takes the C# name and, for a parameter with no description, falls back to
/// repeating its endpoint's summary.
/// </para>
/// </remarks>
public sealed record BrandStyleGuideVersionComparisonViewModel(
    [property: FromQuery(Name = "from")]
    [property: Description("Required. The version number whose content is reported as the 'from' side, as the history lists it.")]
    int? From = null,
    [property: FromQuery(Name = "to")]
    [property: Description("Required. The version number reported as the 'to' side. It may be lower than 'from', which reverses the reading, and it may equal it.")]
    int? To = null);

/// <summary>
/// Shape validation for <see cref="BrandStyleGuideVersionComparisonViewModel"/>: both parameters are
/// required, and a version number is at least 1.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Whether the numbers name versions that exist is not a question a validator can ask.</strong> It
/// does not know which workspace was resolved or which guide the route named, and a lookup from here would be
/// a query outside the seam. Business settles it against the guide the route actually resolved and answers
/// <see cref="BrandErrorCodes.GuideVersionNotFound"/>.
/// </para>
/// <para>
/// <strong><c>from</c> equal to <c>to</c> is accepted.</strong> It is a wasteful request and a truthful one:
/// the answer is a comparison with no changes in it. Refusing it would make a client that preselects the same
/// version on both sides handle an error where rendering "no changes" is correct.
/// </para>
/// <para>
/// <strong><c>from</c> greater than <c>to</c> is accepted too.</strong> Reading a newer version as the
/// left-hand side is what a creator weighing a revert is doing, and the response names which version is
/// which, so nothing about the answer is ambiguous.
/// </para>
/// <para>
/// The 1 floor matches the first version number a guide is created with, so a number no version could carry
/// is refused at the edge rather than looked up and missed. Property names are overridden to the query
/// parameters the caller actually sent.
/// </para>
/// </remarks>
public sealed class BrandStyleGuideVersionComparisonViewModelValidator
    : AbstractValidator<BrandStyleGuideVersionComparisonViewModel>
{
    public BrandStyleGuideVersionComparisonViewModelValidator()
    {
        RuleFor(model => model.From)
            .NotNull()
            .WithMessage("Name the version to compare from.")
            .GreaterThanOrEqualTo(1)
            .WithMessage("Version numbers start at 1.")
            .OverridePropertyName("from");

        RuleFor(model => model.To)
            .NotNull()
            .WithMessage("Name the version to compare to.")
            .GreaterThanOrEqualTo(1)
            .WithMessage("Version numbers start at 1.")
            .OverridePropertyName("to");
    }
}
