using System.ComponentModel;
using CreatorPantry.Domain.Managers.Patching;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// One source for a creative context to name: a kind, and that kind's ids.
/// </summary>
/// <remarks>
/// Send the ids that belong to <see cref="Kind"/> and no others. There is no workspace here and no field for
/// one — every id is resolved inside the workspace named by the route.
/// </remarks>
public sealed record CreativeContextReferenceInputViewModel
{
    /// <summary>What is being named. <c>SocialPackage</c> is not accepted yet.</summary>
    public CreativeContextReferenceKind? Kind { get; init; }

    /// <summary>
    /// What the reference is for: something the work draws on, or something it produced. Omit for
    /// <c>Source</c>, which is what a source named without saying is.
    /// </summary>
    public CreativeContextReferencePurpose? Purpose { get; init; }

    /// <summary>For <c>Recipe</c>: the recipe.</summary>
    public Guid? RecipeId { get; init; }

    /// <summary>For <c>Recipe</c>, optionally: the version to pin. Omit to name the recipe alone.</summary>
    public Guid? RecipeVersionId { get; init; }

    /// <summary>For <c>RecipeConcept</c>: the concept request the concept came from.</summary>
    public Guid? ConceptRequestId { get; init; }

    /// <summary>For <c>RecipeConcept</c>: the chosen concept.</summary>
    public Guid? ConceptId { get; init; }

    /// <summary>For <c>DamAsset</c>: the library asset.</summary>
    public Guid? MediaAssetId { get; init; }

    /// <summary>For <c>DamAsset</c>, optionally: the version to pin. Omit to follow the current one.</summary>
    public int? MediaAssetVersionNumber { get; init; }

    /// <summary>For <c>GeneratedImage</c>: the staged or kept generated image.</summary>
    public Guid? GeneratedImageId { get; init; }

    /// <summary>For <c>PromptRecord</c>: the saved prompt.</summary>
    public Guid? PromptRecordId { get; init; }
}

/// <summary>
/// Starts a creative context. Every field is optional: a context may begin as nothing but the one source it was
/// handed.
/// </summary>
public sealed record CreateCreativeContextViewModel
{
    /// <summary>The creator's own working name for the piece.</summary>
    public string? WorkingTitle { get; init; }

    /// <summary>The picture the creator has in mind, in their words.</summary>
    public string? PictureBrief { get; init; }

    /// <summary>The channels the work is for, in order. Content channel keys such as <c>instagram</c>.</summary>
    public IReadOnlyList<string?>? ChannelKeys { get; init; }

    /// <summary>The day of the week the work is for.</summary>
    public DayOfWeek? Day { get; init; }

    /// <summary>One of this workspace's weekly theme keys.</summary>
    public string? WeeklyThemeKey { get; init; }

    /// <summary>
    /// The source this context starts from, so handing work from one screen to another is a single call.
    /// </summary>
    public CreativeContextReferenceInputViewModel? From { get; init; }
}

/// <summary>
/// A partial edit of a creative context. A field left out is untouched; a field sent as <c>null</c> is cleared.
/// </summary>
public sealed record PatchCreativeContextViewModel
{
    /// <summary>The <c>concurrencyToken</c> from the read this edit was composed against. Required.</summary>
    public string? ExpectedConcurrencyToken { get; init; }

    public PatchField<string?> WorkingTitle { get; init; }

    public PatchField<string?> PictureBrief { get; init; }

    /// <summary>
    /// What the creator chose to work from: <c>Description</c>, <c>Idea</c> or <c>Combined</c>. <c>null</c>
    /// clears the choice.
    /// </summary>
    public PatchField<CreativeContextBriefSource?> BriefSource { get; init; }

    /// <summary>
    /// The brief the creator chose to work from, as they last left it. Never changes <c>pictureBrief</c>.
    /// </summary>
    public PatchField<string?> WorkingBrief { get; init; }

    /// <summary>Replaces the ordered set of channels. <c>null</c> or an empty list clears it.</summary>
    public PatchField<IReadOnlyList<string?>?> ChannelKeys { get; init; }

    public PatchField<DayOfWeek?> Day { get; init; }

    public PatchField<string?> WeeklyThemeKey { get; init; }

    /// <summary><c>true</c> takes the context out of the recent list; <c>false</c> puts it back.</summary>
    public PatchField<bool?> Archived { get; init; }
}

/// <summary>Adds one source to a creative context, after those it already names.</summary>
public sealed record AddCreativeContextReferenceViewModel
{
    /// <summary>The <c>concurrencyToken</c> from the read this edit was composed against. Required.</summary>
    public string? ExpectedConcurrencyToken { get; init; }

    public CreativeContextReferenceInputViewModel? Reference { get; init; }
}

/// <summary>
/// The paging of the recent list. Carries no workspace: that comes from the route.
/// </summary>
public sealed record CreativeContextListViewModel(
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);
