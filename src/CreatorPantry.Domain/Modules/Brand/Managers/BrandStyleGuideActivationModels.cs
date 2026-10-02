using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The body of <c>POST .../brand-style-guides/{guideId}/versions/{versionNumber}/activation</c>: making one
/// approved version the workspace's default brand guide.
/// </summary>
/// <remarks>
/// <para>
/// There is no field for a workspace, a guide or a version number. The workspace comes from the resolved
/// context, and the other two are route segments — a body that also carried them would be a second source of
/// truth and a way for the two to disagree.
/// </para>
/// <para>
/// <strong>No target state either.</strong> There is one default per workspace and one direction to move it,
/// so the only thing to say is which version it should point at, and the route says that.
/// </para>
/// </remarks>
public sealed record ActivateBrandStyleGuideVersionViewModel
{
    /// <summary>
    /// That a person decided this. Required, and must be <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Not redundant beside <see cref="ExpectedActiveVersionId"/>, which says what the caller believes the
    /// world looks like rather than that they meant to change it. Activation repoints what every later
    /// generation in the workspace is grounded on, and it is the one brand decision a workspace makes, so an
    /// API client has to state the step a creator took in the interface rather than reach it by sending a
    /// well-formed body (publishing.md's confirmation rule, applied to the guide that governs the copy).
    /// </remarks>
    [Description("Must be true. States that a person confirmed this activation; a request without it is refused.")]
    public bool? Confirmed { get; init; }

    /// <summary>
    /// The id of the version the caller believes is this workspace's default right now, or <c>null</c> if they
    /// believe it has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The concurrency check, and an identity rather than the default row's <c>RowVersion</c> for a reason the
    /// token cannot cover: when the current default belongs to a <em>different</em> guide, this guide's read
    /// publishes <c>activeVersion: null</c> and no token at all, so there would be nothing for the caller to
    /// quote. A version id is published in both places a caller already looks — <c>activeVersion.id</c> on the
    /// guide read, and the history row whose <c>isActive</c> is true.
    /// </para>
    /// <para>
    /// <strong>Omitting it is a claim, not a waiver.</strong> Absent and explicitly null both mean "this
    /// workspace has no default", which succeeds only when that is true — so a caller who forgot the field
    /// cannot silently replace a default they never saw. The mismatch refusal names the version that actually
    /// holds it.
    /// </para>
    /// </remarks>
    [Description("The id of the version the caller believes is currently active. Omit it only to assert the workspace has no active version.")]
    public Guid? ExpectedActiveVersionId { get; init; }

    /// <summary>The activator's own note on why this version. Optional.</summary>
    /// <remarks>
    /// Optional because <c>BrandStyleGuideDefault.Reason</c> is nullable by design, and stored there as the
    /// creator's own words. It never reaches the audit summary: free text about a private guide is not
    /// something an audit row can promise is safe to display.
    /// </remarks>
    [Description("Why this version is being activated, in the caller's own words. Stored on the activation.")]
    public string? Reason { get; init; }
}

/// <summary>
/// Shape validation for <see cref="ActivateBrandStyleGuideVersionViewModel"/>.
/// </summary>
/// <remarks>
/// Shape only. Whether the version exists, is approved, cites current sources, says anything at all, and is
/// the one the caller expected are all facts about the workspace's data, and backend.md keeps those in
/// Business. What is left here is that the confirmation was given, the expected id could have been issued, and
/// the reason fits the column.
/// </remarks>
public sealed class ActivateBrandStyleGuideVersionViewModelValidator
    : AbstractValidator<ActivateBrandStyleGuideVersionViewModel>
{
    public ActivateBrandStyleGuideVersionViewModelValidator()
    {
        // Equal(true) rather than NotNull: false is a well-formed answer meaning "not confirmed", and it has
        // to be refused for the same reason an absent field is.
        RuleFor(model => model.Confirmed)
            .Equal(true)
            .WithMessage("Confirm the activation before sending it.");

        // Only when present: null is the meaningful claim that the workspace has no active version, and
        // Business is what finds out whether that claim is true. Guid.Empty is not an id this API issues.
        RuleFor(model => model.ExpectedActiveVersionId)
            .NotEqual(Guid.Empty)
                .WithMessage("That is not a brand style guide version id this API issued.")
            .When(model => model.ExpectedActiveVersionId is not null);

        RuleFor(model => model.Reason)
            .MaximumLength(BrandPolicy.ReasonMaxLength)
            .When(model => model.Reason is not null);
    }
}

/// <summary>One guide version named as an activation's subject or as what it replaced.</summary>
/// <remarks>
/// Carries the guide as well as the version, because the default it describes may belong to a different guide
/// than the one in the route — which is the whole reason <c>replaced</c> is worth returning.
/// </remarks>
public sealed record BrandStyleGuideActivatedVersionServiceModel(Guid GuideId, Guid VersionId, int VersionNumber);

/// <summary>
/// What one activation did: which version is now the workspace default, who made it so, and what it moved off.
/// </summary>
/// <param name="Replaced">
/// The version that held the default before this request, or null when the workspace had none. Also what
/// <see cref="AlreadyActive"/> points at when nothing moved.
/// </param>
/// <param name="AlreadyActive">
/// True when the named version already held the default, so nothing was written — no new
/// <paramref name="ActivatedAt"/> and no audit entry. Re-activating what is already active is not a change,
/// and an audit trail full of no-ops is harder to read than one without them.
/// </param>
/// <param name="ActivatedByMembershipId">
/// The membership that activated it. A membership id, never a name or an address — and on a no-op it is
/// whoever activated it originally, not the caller.
/// </param>
public sealed record BrandStyleGuideActivationResultServiceModel(
    Guid GuideId,
    Guid VersionId,
    int VersionNumber,
    DateTimeOffset ActivatedAt,
    Guid ActivatedByMembershipId,
    string? Reason,
    bool AlreadyActive,
    BrandStyleGuideActivatedVersionServiceModel? Replaced);
