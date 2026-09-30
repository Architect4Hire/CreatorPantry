using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of <c>POST .../test-runs/{testRunId}/issues/{issueId}/resolution</c>: what was done about a
/// problem a test found.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No concurrency token, and this is the second write in the module without one</strong> (the first is
/// a duplicate). Nothing is being overwritten: this appends a resolution beside the issue and rewrites neither
/// the issue nor the observation it came from. The conflict this route does have is a different one — an issue
/// already resolved — and the database answers it through the one-per-issue unique index rather than through a
/// token somebody has to quote.
/// </para>
/// <para>
/// <strong>Nothing here describes the issue.</strong> The issue says what went wrong; this says what was done
/// about it. A body that could restate the problem would be a second copy of a fact that can then disagree
/// with the first, and rewriting the tester's words from the resolution seam is exactly what TESTRUN-002
/// forbids.
/// </para>
/// </remarks>
public sealed record ResolveTestIssueViewModel
{
    /// <summary>How the issue was disposed of. Required; there is no safe default.</summary>
    public TestIssueResolutionKind? Kind { get; init; }

    /// <summary>What was done, in the words of whoever did it.</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// The version carrying the correction, by number as the history lists it. Optional, and only meaningful
    /// for <see cref="TestIssueResolutionKind.Fixed"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A version <em>number</em> rather than an id, matching every other route in this module that names one.
    /// It must belong to the recipe the test was run against — a rule the database enforces through the
    /// composite key, not only this seam.
    /// </para>
    /// <para>
    /// It must also be <em>later</em> than the version that was tested, because a version written before the
    /// problem was found cannot contain its fix. That is refused unless
    /// <see cref="PredatingVersionOverrideReason"/> says why it should not be.
    /// </para>
    /// </remarks>
    public int? ResolutionVersionNumber { get; init; }

    /// <summary>
    /// Why a correction version at or before the tested version was named anyway.
    /// </summary>
    /// <remarks>
    /// The ordinary reading of such a version is that somebody cited the wrong number. It is not always: a
    /// creator may be recording that an older version had it right all along and the regression came later.
    /// The override exists so that case can be stated rather than forced through, and it stores the reason
    /// rather than a flag — a boolean would record that somebody overrode the check and leave the next reader
    /// unable to tell a considered decision from a mis-click.
    /// </remarks>
    public string? PredatingVersionOverrideReason { get; init; }
}

/// <summary>
/// Shape and format validation for <see cref="ResolveTestIssueViewModel"/>.
/// </summary>
/// <remarks>
/// What is deliberately not here: whether the issue exists, whether it is already resolved, whether the named
/// version belongs to this recipe, and whether it predates the tested version. All four need a lookup, so they
/// belong below, where a refusal can say which run, which issue and which version it looked for.
/// </remarks>
public sealed class ResolveTestIssueViewModelValidator : AbstractValidator<ResolveTestIssueViewModel>
{
    public ResolveTestIssueViewModelValidator()
    {
        RuleFor(model => model.Kind)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Say how the issue was resolved.")
            .Must(kind => Enum.IsDefined(kind!.Value) && kind != default)
                .WithMessage("That is not a resolution.");

        RuleFor(model => (model.Notes ?? string.Empty).Trim())
            .MaximumLength(TestRunPolicy.ResolutionNotesMaxLength)
                .WithMessage($"Resolution notes can be at most {TestRunPolicy.ResolutionNotesMaxLength} characters.")
            .OverridePropertyName(nameof(ResolveTestIssueViewModel.Notes));

        // The floor matches CK_RecipeVersions_VersionNumber_Positive, so a number no version could carry is a
        // field error naming the parameter rather than a lookup that finds nothing.
        RuleFor(model => model.ResolutionVersionNumber)
            .GreaterThanOrEqualTo(RecipePolicy.FirstVersionNumber)
                .WithMessage($"A version number starts at {RecipePolicy.FirstVersionNumber}.")
            .When(model => model.ResolutionVersionNumber is not null);

        // Mirrors CK_TestIssueResolutions_Version_Kind: a correction version belongs to a resolution that
        // corrected something. Without this the two columns can disagree, and a history can report an issue as
        // declined while pointing at the version that fixed it.
        RuleFor(model => model.ResolutionVersionNumber)
            .Must((model, _) => model.Kind == TestIssueResolutionKind.Fixed)
                .WithMessage("Only a fix names the version that corrected it.")
            .When(model => model.ResolutionVersionNumber is not null && model.Kind is not null);

        RuleFor(model => (model.PredatingVersionOverrideReason ?? string.Empty).Trim())
            .MaximumLength(TestRunPolicy.PredatingVersionOverrideReasonMaxLength)
                .WithMessage(
                    "An override reason can be at most "
                        + $"{TestRunPolicy.PredatingVersionOverrideReasonMaxLength} characters.")
            .OverridePropertyName(nameof(ResolveTestIssueViewModel.PredatingVersionOverrideReason));

        // Mirrors CK_TestIssueResolutions_Override_RequiresVersion: a reason for overriding nothing is a reason
        // for nothing.
        RuleFor(model => model.PredatingVersionOverrideReason)
            .Must((model, _) => model.ResolutionVersionNumber is not null)
                .WithMessage("An override reason needs the version it is overriding.")
            .When(model => !string.IsNullOrWhiteSpace(model.PredatingVersionOverrideReason));
    }
}
