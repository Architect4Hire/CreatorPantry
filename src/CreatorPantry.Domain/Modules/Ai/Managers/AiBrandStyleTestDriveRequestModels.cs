using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a client sends to ask for a style test drive (11A.24).
/// </summary>
/// <remarks>
/// <para>
/// Three fields, two of which identify a guide version the creator picked from their own history. There is no
/// scope, no model, no channel, no prompt and no workspace: the scope is fixed server-side at
/// <see cref="AiOperationScope.NotApplicable"/>, the model is the deployment's, the channel is deliberately
/// unset (see <see cref="BrandContextSelection.SectionKeysFor"/>), and the workspace is the route's.
/// </para>
/// <para>
/// <strong>The guide version is required.</strong> Every other brand-grounded seam treats it as optional and
/// falls back to the workspace's active version; a test drive cannot, because trying a version out before
/// activating it is the whole point, and a request that silently tested the active guide instead would answer a
/// question the creator did not ask.
/// </para>
/// </remarks>
public sealed class RequestBrandStyleTestDriveViewModel
{
    /// <summary>The guide to test. Resolved under the workspace filter, so a neighbour's id is "not found".</summary>
    public Guid GuideId { get; set; }

    /// <summary>
    /// Its version number as the history lists it — never a version id, for the reason
    /// <see cref="BrandGuideSelection"/> gives.
    /// </summary>
    public int VersionNumber { get; set; }

    /// <summary>
    /// What the samples should be about, in the creator's own words, or null for
    /// <see cref="BrandStyleTestDriveSubject.Default"/>.
    /// </summary>
    /// <remarks>
    /// Optional, short, and the same for both halves of the comparison. It exists so a creator sees their voice
    /// on their own food rather than on the platform's example; it is not a brief, and nothing else about the
    /// generation can be steered from it.
    /// </remarks>
    [Description("What the samples are about. Optional; a short phrase, not a brief.")]
    public string? Subject { get; set; }
}

public sealed class RequestBrandStyleTestDriveViewModelValidator
    : AbstractValidator<RequestBrandStyleTestDriveViewModel>
{
    public RequestBrandStyleTestDriveViewModelValidator()
    {
        RuleFor(model => model.GuideId)
            .NotEmpty().WithMessage("Name the guide to try out.");

        RuleFor(model => model.VersionNumber)
            .GreaterThan(0).WithMessage("Name the version of that guide to try out.");

        RuleFor(model => model.Subject)
            .MaximumLength(AiPolicy.StyleSampleSubjectMaxLength)
            .WithMessage($"Keep the subject to {AiPolicy.StyleSampleSubjectMaxLength} characters or fewer.");
    }
}

/// <summary>Stable error codes this seam introduces. Renaming one is a breaking API change.</summary>
public static class AiBrandStyleTestDriveRequestErrors
{
    public const string RequestInvalid = "ai.brandStyleTestDrive.invalid_request";

    public const string TaskNotEnabled = "ai.brandStyleTestDrive.not_enabled";

    /// <summary>No such guide or version in this workspace. Deliberately indistinguishable (tenancy.md).</summary>
    public const string GuideNotFound = "ai.brandStyleTestDrive.guide.not_found";

    /// <summary>
    /// The named version has nothing for a test drive to demonstrate.
    /// </summary>
    /// <remarks>
    /// Checked before any provider call, so a creator whose guide is still empty is told rather than charged for
    /// two generations that would produce two identical columns.
    /// </remarks>
    public const string GuideHasNoGuidance = "ai.brandStyleTestDriveGuide.invalid_request";

    public const string RequestNotFound = "ai.brandStyleTestDriveRequest.not_found";
}

/// <summary>The platform's own subject, used when the creator names none.</summary>
/// <remarks>
/// <para>
/// Fixed, and identical in both halves. A comparison whose subject moved between the two calls would be
/// measuring the subject; this is the control.
/// </para>
/// <para>
/// Deliberately ordinary and unopinionated — a dish with no strong regional, dietary or seasonal character, so
/// that whatever character the right-hand column has came from the creator's guide and not from the example.
/// </para>
/// </remarks>
public static class BrandStyleTestDriveSubject
{
    public const string Default = "a one-pan lemon chicken for a weeknight";

    /// <summary>The subject this request will use: the creator's, trimmed, or the platform's.</summary>
    public static string Resolve(string? supplied) =>
        string.IsNullOrWhiteSpace(supplied) ? Default : supplied.Trim();
}

/// <summary>
/// The test drive's own capability-specific field, as it travels in <c>AiOperation.TaskInputsJson</c>.
/// </summary>
/// <remarks>
/// The guide and version travel through <see cref="BrandContextRequestInputs"/>, which every brand-grounded
/// task shares. Only the subject is this capability's own, so only it is named here.
/// </remarks>
public static class BrandStyleTestDriveInputs
{
    public const string Subject = "styleSampleSubject";

    /// <summary>The subject an operation was requested with, resolved to the platform's when it named none.</summary>
    public static string ReadSubject(IReadOnlyDictionary<string, string>? inputs) =>
        BrandStyleTestDriveSubject.Resolve(inputs?.GetValueOrDefault(Subject));
}
