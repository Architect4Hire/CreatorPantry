using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The body of <c>POST .../brand-style-guides/{guideId}/versions/{versionNumber}/approval</c>: marking one
/// version of a guide finished, which is what lets an Owner make it the workspace default.
/// </summary>
/// <remarks>
/// <para>
/// There is no field for a workspace, a guide or a version number. The workspace comes from the resolved
/// context, and the other two are route segments — a body that also carried them would be a second source of
/// truth and a way for the two to disagree.
/// </para>
/// <para>
/// <strong>No expected-state field, unlike activation.</strong> Activation moves the workspace's one default
/// and has to say which version the caller believes holds it. An approval is write-once and names one
/// version, so there is no state it could be racing to replace: approving a version that is already approved
/// is the same answer, and approving one version says nothing about any other.
/// </para>
/// <para>
/// <strong>And no field to withdraw one.</strong> <c>BrandStyleGuideApproval</c> states that an approval is
/// never withdrawn: moving off an approved version means approving and activating another, which keeps every
/// generation that cited this one traceable to a version that was approved when it was used.
/// </para>
/// </remarks>
public sealed record ApproveBrandStyleGuideVersionViewModel
{
    /// <summary>
    /// That a person decided this. Required, and must be <c>true</c>.
    /// </summary>
    /// <remarks>
    /// An approval is what the workspace's one activation decision is defined over, so an API client has to
    /// state the step a creator took in the interface rather than reach it by sending a well-formed body —
    /// the same confirmation rule activation applies, one decision earlier.
    /// </remarks>
    [Description("Must be true. States that a person confirmed this approval; a request without it is refused.")]
    public bool? Confirmed { get; init; }

    /// <summary>The approver's own note on why this version is finished. Optional.</summary>
    /// <remarks>
    /// Optional because <c>BrandStyleGuideApproval.Reason</c> is nullable by design, and stored there as the
    /// creator's own words. It never reaches the audit summary: free text about a private guide is not
    /// something an audit row can promise is safe to display.
    /// </remarks>
    [Description("Why this version is being approved, in the caller's own words. Stored on the approval.")]
    public string? Reason { get; init; }
}

/// <summary>
/// Shape validation for <see cref="ApproveBrandStyleGuideVersionViewModel"/>.
/// </summary>
/// <remarks>
/// Shape only. Whether the guide exists, whether it is archived, whether the version exists, whether it says
/// anything at all, and whether it is approved already are all facts about the workspace's data, and
/// backend.md keeps those in Business. What is left here is that the confirmation was given and the reason
/// fits the column.
/// </remarks>
public sealed class ApproveBrandStyleGuideVersionViewModelValidator
    : AbstractValidator<ApproveBrandStyleGuideVersionViewModel>
{
    public ApproveBrandStyleGuideVersionViewModelValidator()
    {
        // Equal(true) rather than NotNull: false is a well-formed answer meaning "not confirmed", and it has
        // to be refused for the same reason an absent field is.
        RuleFor(model => model.Confirmed)
            .Equal(true)
            .WithMessage("Confirm the approval before sending it.");

        RuleFor(model => model.Reason)
            .MaximumLength(BrandPolicy.ReasonMaxLength)
            .When(model => model.Reason is not null);
    }
}

/// <summary>
/// What one approval did: which version is approved, who approved it, and when.
/// </summary>
/// <param name="AlreadyApproved">
/// True when the version was already approved, so nothing was written — no new <paramref name="ApprovedAt"/>
/// and no audit entry. Approving what is already approved is not a change, and an audit trail full of no-ops
/// is harder to read than one without them.
/// </param>
/// <param name="ApprovedByMembershipId">
/// The membership that approved it. A membership id, never a name or an address — and on a no-op it is
/// whoever approved it originally, not the caller.
/// </param>
/// <param name="Reason">
/// The approver's own words, or null. On a no-op the original approver's, for the same reason.
/// </param>
/// <remarks>
/// It says nothing about the workspace default. An approved version is one an Owner <em>may</em> activate, and
/// reporting anything about activation here would read as though this had done it.
/// </remarks>
public sealed record BrandStyleGuideApprovalResultServiceModel(
    Guid GuideId,
    Guid VersionId,
    int VersionNumber,
    DateTimeOffset ApprovedAt,
    Guid ApprovedByMembershipId,
    string? Reason,
    bool AlreadyApproved);
