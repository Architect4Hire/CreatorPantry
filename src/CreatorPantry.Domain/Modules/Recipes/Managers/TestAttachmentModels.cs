using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of
/// <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/test-runs/{testRunId}/attachments</c>
/// (RCPUB-005): one library asset attached to one recorded test as evidence of what it produced.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No workspace, no recipe and no test.</strong> All three come from the route and the caller's
/// membership. The asset is resolved inside that workspace before anything is written, and the foreign key
/// behind the attachment makes a cross-workspace one unrepresentable (tenancy.md).
/// </para>
/// <para>
/// <strong>No role.</strong> A test's photographs are evidence, and none of them is the lead image of anything
/// — that belongs to the recipe, on its own links.
/// </para>
/// </remarks>
public sealed record AttachTestImageViewModel
{
    /// <summary>The asset to attach. Required, and must be in this workspace's library.</summary>
    public Guid? MediaAssetId { get; init; }

    /// <summary>
    /// The version of the asset to pin, from 1. Omit it to follow whichever version is current.
    /// </summary>
    public int? VersionNumber { get; init; }

    /// <summary>The issue of this test the picture illustrates. Optional; omit it for a picture of the test as a whole.</summary>
    public Guid? TestIssueId { get; init; }

    /// <summary>The caption written for this test. Optional. Not alt text, which belongs to the asset.</summary>
    public string? Caption { get; init; }
}

/// <summary>Shape checks on an attach request.</summary>
public sealed class AttachTestImageViewModelValidator : AbstractValidator<AttachTestImageViewModel>
{
    public AttachTestImageViewModelValidator()
    {
        RuleFor(model => model.MediaAssetId)
            .Must(id => id is { } value && value != Guid.Empty)
            .WithMessage("Choose the picture to attach.");

        RuleFor(model => model.VersionNumber)
            .GreaterThanOrEqualTo(1)
            .When(model => model.VersionNumber is not null)
            .WithMessage("Version numbers start at 1.");

        RuleFor(model => model.TestIssueId)
            .NotEqual(Guid.Empty)
            .When(model => model.TestIssueId is not null)
            .WithMessage("That is not an issue of this test.");

        RuleFor(model => (model.Caption ?? string.Empty).Trim())
            .MaximumLength(RecipePolicy.CaptionMaxLength)
            .OverridePropertyName(nameof(AttachTestImageViewModel.Caption));
    }
}

/// <summary>An attach request, validated and normalized.</summary>
public sealed record CanonicalTestAttachment
{
    public required Guid MediaAssetId { get; init; }

    public int? VersionNumber { get; init; }

    public Guid? TestIssueId { get; init; }

    public string? Caption { get; init; }

    public static CanonicalTestAttachment From(AttachTestImageViewModel model)
    {
        var caption = model.Caption?.Trim();

        return new CanonicalTestAttachment
        {
            // Non-null by validation.
            MediaAssetId = model.MediaAssetId!.Value,
            VersionNumber = model.VersionNumber,
            TestIssueId = model.TestIssueId,
            Caption = string.IsNullOrEmpty(caption) ? null : caption,
        };
    }

    /// <summary>What this request asked for, for the idempotency fingerprint. Both route values are part of it.</summary>
    public object Fingerprint(Guid recipeId, Guid testRunId) => new
    {
        RecipeId = recipeId,
        TestRunId = testRunId,
        MediaAssetId,
        VersionNumber,
        TestIssueId,
        Caption,
    };
}

/// <summary>
/// One picture attached to a recorded test, as a reference rather than as content.
/// </summary>
/// <remarks>
/// <see cref="MediaAssetId"/> and, when pinned, a version number — never a storage URL, a container path or a
/// signed link. The bytes are read through the media routes, by asset and version, with their own
/// authorization on every request (media.md).
/// </remarks>
/// <param name="Id">The attachment's own id. What a detach names; not an asset id.</param>
/// <param name="MediaAssetVersionNumber">The pinned version, or <c>null</c> when it follows whichever is current.</param>
/// <param name="TestIssueId">The issue of the test this illustrates, or <c>null</c> for the test as a whole.</param>
public sealed record TestAttachmentServiceModel(
    Guid Id,
    Guid MediaAssetId,
    int? MediaAssetVersionNumber,
    Guid? TestIssueId,
    int SortOrder,
    string? Caption);

/// <summary>What the attach and detach commands need to know about the test they act on.</summary>
/// <param name="RecipeStatus">The status of the recipe the test belongs to, which decides whether it accepts changes.</param>
public sealed record TestAttachmentRunRecord(RecipeStatus RecipeStatus);

/// <summary>How a write of an attachment ended.</summary>
public enum TestAttachmentWriteOutcome
{
    Committed = 0,

    /// <summary>
    /// Another attachment was saved to the same test at the same moment and took the position. Nothing was
    /// written.
    /// </summary>
    PositionTaken = 1,
}
