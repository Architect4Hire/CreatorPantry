using System.ComponentModel;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>One section of a plan, as a creator submits it.</summary>
/// <remarks>
/// A role, a heading, an intent and — for a newsletter item — the source it covers. It cannot carry a body, a
/// settled flag, provenance or a workspace: a plan edit changes the plan and nothing else.
/// </remarks>
public sealed class LongFormPlanSectionInputViewModel
{
    [Description("The section's id when it is one the plan already has. Omit it for a section being added.")]
    public Guid? Id { get; set; }

    [Description("What the section is for, from the roles this kind of piece allows.")]
    public LongFormSectionRole? Role { get; set; }

    [Description("The heading a reader would see, on one line.")]
    public string? Heading { get; set; }

    [Description("One line saying what the section will cover.")]
    public string? Intent { get; set; }

    [Description("For a newsletter item, the creative-context reference of the recipe it covers, which an item already in the plan keeps. Omit it for every other section.")]
    public Guid? SourceReferenceId { get; set; }
}

/// <summary>The creator's whole plan, in order, saved as a new revision (LF.2.2).</summary>
public sealed class PatchLongFormPlanViewModel
{
    [Description("The concurrencyToken of the draft this edit was composed against.")]
    public string? ExpectedConcurrencyToken { get; set; }

    [Description("Every section of the plan, in the order the piece should read. A section left out is removed.")]
    public IReadOnlyList<LongFormPlanSectionInputViewModel>? Sections { get; set; }
}

/// <summary>Approval of one revision of a plan (LF.2.2).</summary>
public sealed class ApproveLongFormPlanViewModel
{
    [Description("The concurrencyToken of the draft this approval was composed against.")]
    public string? ExpectedConcurrencyToken { get; set; }

    [Description("The revision being approved. It must be the draft's latest.")]
    public int RevisionNumber { get; set; }
}

/// <summary>Which recent drafts to list.</summary>
public sealed class LongFormDraftListViewModel
{
    [Description("How many to return. Clamped to the page size the server allows.")]
    public int? Limit { get; set; }

    [Description("The previous page's nextCursor, exactly as it was returned.")]
    public string? Cursor { get; set; }

    [Description("Only drafts of this kind.")]
    public LongFormKind? Kind { get; set; }

    [Description("Only drafts written for this creative context.")]
    public Guid? CreativeContextId { get; set; }
}

public sealed class PatchLongFormPlanViewModelValidator : AbstractValidator<PatchLongFormPlanViewModel>
{
    public PatchLongFormPlanViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(CreativeContextConcurrencyToken.IsWellFormed)
            .WithMessage("Send the concurrencyToken of the draft this edit was composed against.")
            .OverridePropertyName("expectedConcurrencyToken");

        RuleFor(model => model.Sections)
            .NotNull()
            .WithMessage("Send the whole plan.")
            .Must(sections => sections is null || sections.Count is >= 1 and <= LongFormDraftPolicy.MaxPlanSections)
            .WithMessage($"A plan has between 1 and {LongFormDraftPolicy.MaxPlanSections} sections.")
            .Must(sections => sections is null
                || sections.Where(section => section.Id is not null).Select(section => section.Id).Distinct().Count()
                    == sections.Count(section => section.Id is not null))
            .WithMessage("Name each existing section once.")
            .OverridePropertyName("sections");

        RuleForEach(model => model.Sections)
            .ChildRules(section =>
            {
                section.RuleFor(item => item.Role)
                    .Must(role => role is { } value && Enum.IsDefined(value))
                    .WithMessage("Give the section one of the roles a plan can have.");

                // Storage bounds, not the plan's own caps. A heading over its cap is a plan that is not ready
                // to approve, which the creator is told on the draft; this refuses only what could not be kept.
                section.RuleFor(item => item.Heading)
                    .NotNull()
                    .WithMessage("Give the section a heading, even an empty one while you work.")
                    .Must(heading => heading is null || heading.Length <= LongFormDraftPolicy.HeadingStorageMaxLength)
                    .WithMessage("That heading is far too long to keep.");

                section.RuleFor(item => item.Intent)
                    .NotNull()
                    .WithMessage("Give the section an intent, even an empty one while you work.")
                    .Must(intent => intent is null || intent.Length <= LongFormDraftPolicy.IntentStorageMaxLength)
                    .WithMessage("That intent is far too long to keep.");
            })
            .OverridePropertyName("sections");
    }
}

public sealed class ApproveLongFormPlanViewModelValidator : AbstractValidator<ApproveLongFormPlanViewModel>
{
    public ApproveLongFormPlanViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(CreativeContextConcurrencyToken.IsWellFormed)
            .WithMessage("Send the concurrencyToken of the draft this approval was composed against.")
            .OverridePropertyName("expectedConcurrencyToken");

        RuleFor(model => model.RevisionNumber)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Name the revision being approved.")
            .OverridePropertyName("revisionNumber");
    }
}

public sealed class LongFormDraftListViewModelValidator : AbstractValidator<LongFormDraftListViewModel>
{
    public LongFormDraftListViewModelValidator()
    {
        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");

        RuleFor(model => model.Kind)
            .Must(kind => kind is null || Enum.IsDefined(kind.Value))
            .WithMessage("That is not a kind of long-form piece.")
            .OverridePropertyName("kind");
    }
}

/// <summary>Bounds on what the draft seam will take. Storage limits, not the plan's shape rules.</summary>
public static class LongFormDraftPolicy
{
    /// <summary>The most sections a submitted plan may hold: the largest plan any kind's role rules allow.</summary>
    public const int MaxPlanSections = 15;

    /// <summary>
    /// The longest text one section may be saved with, in UTF-16 units.
    /// </summary>
    /// <remarks>
    /// Four times the section ceiling, and the same bound a generated body is held to. Not the ceiling: text
    /// between the two is saved and reported as over it.
    /// </remarks>
    public const int SectionBodyMaxLength = LongFormPolicy.SectionMaxCharacters * 4;

    /// <summary>
    /// The longest heading the seam will keep, in UTF-16 units — ten for each character of the plan's own cap,
    /// so a heading over that cap can still be saved, shown as too long, and fixed.
    /// </summary>
    public const int HeadingStorageMaxLength = LongFormPolicy.HeadingMaxCharacters * 10;

    /// <inheritdoc cref="HeadingStorageMaxLength"/>
    public const int IntentStorageMaxLength = LongFormPolicy.IntentMaxCharacters * 10;
}

/// <summary>One planned section, as a creator is shown it.</summary>
/// <param name="IsWritten">Whether the section has a body yet. The body itself is not part of a plan read.</param>
/// <param name="SourceState">
/// For a newsletter item, whether the piece of work still names the reference it covers. Null on every other
/// section.
/// </param>
public sealed record LongFormPlannedSectionServiceModel(
    Guid Id,
    LongFormSectionRole Role,
    string Heading,
    string Intent,
    bool IsWritten,
    bool Settled,
    Guid? SourceReferenceId,
    LongFormItemSourceState? SourceState);

/// <summary>One revision of a long-form draft, as its plan.</summary>
public sealed record LongFormRevisionServiceModel(
    int RevisionNumber,
    ContentRevisionSource Source,
    DateTimeOffset CreatedAt,
    IReadOnlyList<LongFormPlannedSectionServiceModel> Sections);

/// <summary>One thing that stands between a plan and its approval.</summary>
/// <param name="Code">One of <see cref="LongFormFindingCodes"/>.</param>
/// <param name="SectionId">The section it is about, or null when it is about the plan as a whole.</param>
public sealed record LongFormPlanCheckFindingServiceModel(string Code, Guid? SectionId);

/// <summary>Whether the latest plan could be approved as it stands, and if not, why.</summary>
/// <param name="PolicyVersion">Which shape rules produced this answer.</param>
public sealed record LongFormPlanCheckServiceModel(
    bool IsValid, string PolicyVersion, IReadOnlyList<LongFormPlanCheckFindingServiceModel> Findings);

/// <summary>
/// A long-form draft with its latest revision (LF.2.2). A derivative: nothing here is a recipe fact.
/// </summary>
/// <param name="ConcurrencyToken">Send this back as <c>expectedConcurrencyToken</c> on the next write.</param>
/// <param name="Latest">Null until an outline has landed.</param>
/// <param name="PlanCheck">Null exactly when <paramref name="Latest"/> is.</param>
/// <param name="Framing">A newsletter issue's subject line and note, or null when it has neither.</param>
/// <param name="UnresolvedItems">
/// The items, in plan order, whose reference the piece of work no longer names. Each is still in the plan with
/// everything it had; this is what a creator must be told, and it stops neither an approval nor an acceptance.
/// </param>
public sealed record LongFormDraftServiceModel(
    Guid Id,
    Guid CreativeContextId,
    LongFormKind Kind,
    LongFormDraftStatus Status,
    string ConcurrencyToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    LongFormRevisionServiceModel? Latest,
    LongFormPlanCheckServiceModel? PlanCheck,
    LongFormFramingServiceModel? Framing,
    IReadOnlyList<Guid> UnresolvedItems);

/// <summary>One draft in the recent list. No words of the plan: titles of work live on the creative context.</summary>
public sealed record LongFormDraftSummaryServiceModel(
    Guid Id,
    Guid CreativeContextId,
    LongFormKind Kind,
    LongFormDraftStatus Status,
    int? LatestRevisionNumber,
    DateTimeOffset UpdatedAt);

/// <summary>One row of the recent list, as the repository reads it.</summary>
public sealed record LongFormDraftSummaryRecord(
    Guid Id,
    Guid CreativeContextId,
    LongFormKind Kind,
    LongFormDraftStatus Status,
    int? LatestRevisionNumber,
    DateTimeOffset UpdatedAt) : IReferenceRow
{
    public string SortValue => CreativeContextListPosition.FormatTimestamp(UpdatedAt);

    public string TieBreaker => Id.ToString("D");
}

/// <summary>What the recent list was asked for, with the workspace already bound into its scope.</summary>
public sealed record LongFormDraftListCriteria(
    string Scope,
    LongFormKind? Kind,
    Guid? CreativeContextId,
    CreativeContextListPosition? Position = null,
    int? RequestedLimit = null)
{
    public const string Resource = "long-form-drafts";

    public const string Ordering = "UpdatedDescending";

    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);

    /// <summary>
    /// The filters are part of the scope, so a cursor issued for one filtered list cannot page another.
    /// </summary>
    public static string ScopeFor(Guid workspaceId, LongFormKind? kind, Guid? creativeContextId) =>
        string.Join(
            '|',
            $"resource={Resource}",
            $"workspace={workspaceId:D}",
            $"kind={kind?.ToString() ?? "any"}",
            $"context={creativeContextId?.ToString("D") ?? "any"}",
            $"sort={Ordering}");
}

/// <summary>One section of a generated plan, as the AI module hands it over to be stored.</summary>
public sealed record LongFormGeneratedSectionInput(
    LongFormSectionRole Role, string Heading, string Intent, Guid? SourceReferenceId);

/// <summary>One recipe version a generated plan was written against, and the reference it came from.</summary>
public sealed record LongFormRecipePinInput(Guid CreativeContextReferenceId, Guid RecipeId, Guid RecipeVersionId);

/// <summary>
/// A generated plan and everything it was written against, to be stored as a draft's next revision (LF.2.2).
/// </summary>
/// <remarks>
/// The draft is named by id and created if it does not exist yet, so the plan can land whether or not the
/// request that asked for it finished creating the draft. No workspace: that is the resolved context's.
/// </remarks>
public sealed record LongFormGeneratedPlanInput(
    Guid DraftId,
    Guid CreativeContextId,
    LongFormKind Kind,
    Guid AiProposalId,
    string PromptTemplateId,
    string PromptTemplateVersion,
    string PromptTemplateBodyChecksum,
    Guid? BrandStyleGuideVersionId,
    string? CreativeContextVersion,
    IReadOnlyList<LongFormGeneratedSectionInput> Sections,
    IReadOnlyList<LongFormRecipePinInput> RecipePins);
