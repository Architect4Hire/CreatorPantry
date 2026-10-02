namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Says out loud what an assembled <see cref="BrandContextPackage"/> could not do, or did differently from what
/// the brand setup implies (11A.20).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is what keeps a stale guide from being used silently.</strong> 11A.20 forbids silently selecting a
/// stale guide, and a writing task only reads one — so the answer is not to refuse the generation but to use
/// exactly the version the creator asked for and say which it was. The pairing is deliberate: the version is
/// recorded in provenance, and the fact that it is no longer the active one is surfaced here, where the review
/// panel already shows cautions.
/// </para>
/// <para>
/// <strong>Server findings, added after the model's own warnings</strong> — the ordering
/// <c>SeoPackageAiTaskHandler</c> already uses, and for the same reason: nothing the model says can displace one
/// of these or push it past a warning limit.
/// </para>
/// <para>
/// <strong>Not every conflict is a notice.</strong> <see cref="BrandContextConflict.AudienceOverridesProfile"/>
/// is deliberately silent: the creator typed that audience, so telling them it was used is noise, and the
/// provenance row records both the audience and where it came from. Every other member maps, and
/// <c>BrandContextNoticeTests</c> fails if a new one does neither — the alternative is a conflict the assembler
/// detects and nobody ever sees.
/// </para>
/// </remarks>
public static class BrandContextNotices
{
    public const string GuideVersionNotActive = "brand_context.guide_version_not_active";

    public const string GuideVersionUnapproved = "brand_context.guide_version_unapproved";

    public const string ChannelNotABrandDefault = "brand_context.channel_not_a_brand_default";

    public const string ChannelVariantForAnotherChannelOnly = "brand_context.channel_variant_for_another_channel_only";

    public const string SourceDocumentChannelMismatch = "brand_context.source_document_channel_mismatch";

    public const string SourceDocumentAudienceMismatch = "brand_context.source_document_audience_mismatch";

    public const string NoActiveGuide = "brand_context.no_active_guide";

    public const string NoBrandProfile = "brand_context.no_brand_profile";

    public const string GuideSectionMissing = "brand_context.guide_section_missing";

    public const string NoSourceExcerpts = "brand_context.no_source_excerpts";

    public const string SourceDocumentUnavailable = "brand_context.source_document_unavailable";

    public const string ExcerptOverBudget = "brand_context.excerpt_over_budget";

    public const string GuideSectionOverBudget = "brand_context.guide_section_over_budget";

    public const string NoVisualReferenceText = "brand_context.no_visual_reference_text";

    /// <summary>
    /// The conflicts this translator deliberately does not surface, and which are recorded instead.
    /// </summary>
    /// <remarks>
    /// A named set rather than a fall-through, so leaving a member out of both this and the mapping below is a
    /// test failure rather than silence.
    /// </remarks>
    public static IReadOnlySet<BrandContextConflict> SilentConflicts { get; } =
        new HashSet<BrandContextConflict> { BrandContextConflict.AudienceOverridesProfile };

    /// <summary>
    /// What the creator should be told about this package, in a stable order.
    /// </summary>
    /// <remarks>
    /// None of these warnings names a change: they are about the grounding the whole output rests on, not about
    /// one proposed line. Conflicts come before omissions, and each group keeps its enum order, so two
    /// generations grounded the same way read the same way.
    /// </remarks>
    public static IReadOnlyList<AiOutputWarning> For(BrandContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var warnings = new List<AiOutputWarning>();

        foreach (var conflict in package.Conflicts)
        {
            if (SilentConflicts.Contains(conflict))
            {
                continue;
            }

            var (kind, code, message) = Describe(conflict, package);

            warnings.Add(Warning(kind, code, message));
        }

        foreach (var omission in package.Omissions)
        {
            var (kind, code, message) = Describe(omission, package.TaskType);

            warnings.Add(Warning(kind, code, message));
        }

        return warnings;
    }

    private static AiOutputWarning Warning(AiWarningKind kind, string code, string message) => new()
    {
        Kind = kind,
        Message = $"[{code}] {message}",

        // Deliberately unattached. A grounding caution belongs to the output as a whole, and pinning it to the
        // first change would make the rest look unaffected.
        ChangeIndex = null,
    };

    private static (AiWarningKind Kind, string Code, string Message) Describe(
        BrandContextConflict conflict, BrandContextPackage package) => conflict switch
    {
        // Limitation rather than Assumption: the creator asked for a version and got it, but the brand voice
        // their workspace is currently publishing under is a different one.
        BrandContextConflict.GuideVersionNotActive => (
            AiWarningKind.Limitation,
            GuideVersionNotActive,
            package.GuideVersionNumber is { } number
                ? $"This was written from version {number} of your brand style guide, which is no longer the "
                    + "active version."
                : "This was written from a brand style guide version that is no longer the active one."),

        BrandContextConflict.GuideVersionUnapproved => (
            AiWarningKind.Limitation,
            GuideVersionUnapproved,
            "The brand style guide version this was written from has not been approved."),

        // Assumption: the channel was taken as given although the brand profile does not list it, so the
        // guidance applied to it is the brand's general voice rather than anything channel-specific.
        BrandContextConflict.ChannelNotABrandDefault => (
            AiWarningKind.Assumption,
            ChannelNotABrandDefault,
            "This was written for a channel your brand profile does not list among its defaults."),

        BrandContextConflict.ChannelVariantForAnotherChannelOnly => (
            AiWarningKind.Assumption,
            ChannelVariantForAnotherChannelOnly,
            "Your guide has channel-specific guidance, but only for other channels, so the general voice was "
                + "used instead."),

        BrandContextConflict.SourceDocumentChannelMismatch => (
            AiWarningKind.Assumption,
            SourceDocumentChannelMismatch,
            "Some of the writing samples used were written for a different channel."),

        BrandContextConflict.SourceDocumentAudienceMismatch => (
            AiWarningKind.Assumption,
            SourceDocumentAudienceMismatch,
            "Some of the writing samples used were written for a different audience."),

        // No catch-all behaviour: a member added without a notice and without a place in SilentConflicts fails
        // loudly rather than being dropped where nobody is looking. BrandContextNoticeTests walks the enum, so
        // this throw stays unreachable — it is the guard for the case the test is there to prevent.
        _ => throw new ArgumentOutOfRangeException(
            nameof(conflict),
            conflict,
            "A brand-context conflict has no notice and is not listed as deliberately silent."),
    };

    private static (AiWarningKind Kind, string Code, string Message) Describe(
        BrandContextOmission omission, AiTaskType taskType) =>
        omission switch
        {
            // The image tasks read the guide's visual sections, so the writing-voice wording would mislead.
            BrandContextOmission.NoActiveGuide when BrandContextSelection.IsVisual(taskType) => (
                AiWarningKind.Limitation,
                NoActiveGuide,
                "Your workspace has no active brand style guide, so this was composed without your visual direction."),

            BrandContextOmission.GuideSectionMissing when BrandContextSelection.IsVisual(taskType) => (
                AiWarningKind.Limitation,
                GuideSectionMissing,
                "Your brand style guide does not cover every part of its visual direction that this task would have used."),

            BrandContextOmission.NoSourceExcerpts when BrandContextSelection.IsVisual(taskType) => (
                AiWarningKind.Limitation,
                NoSourceExcerpts,
                "No visual references from your source library were used, so the look rests on your guide alone."),

            BrandContextOmission.NoVisualReferenceText => (
                AiWarningKind.Limitation,
                NoVisualReferenceText,
                "A visual reference you named has no text we could use, so it was not used. Its image was not sent."),

            BrandContextOmission.NoActiveGuide => (
                AiWarningKind.Limitation,
                NoActiveGuide,
                "Your workspace has no active brand style guide, so this was written without one."),

            BrandContextOmission.NoBrandProfile => (
                AiWarningKind.Limitation,
                NoBrandProfile,
                "Your workspace has no brand profile, so no brand name, audience or locale was applied."),

            BrandContextOmission.GuideSectionMissing => (
                AiWarningKind.Limitation,
                GuideSectionMissing,
                "Your brand style guide does not cover every part of its voice that this task would have used."),

            BrandContextOmission.NoSourceExcerpts => (
                AiWarningKind.Limitation,
                NoSourceExcerpts,
                "No writing samples from your source library were used, so the voice rests on your guide alone."),

            BrandContextOmission.SourceDocumentUnavailable => (
                AiWarningKind.Limitation,
                SourceDocumentUnavailable,
                "A source document this was meant to draw on could not be read."),

            // Both budget omissions are Limitation and say the same thing in different words: something relevant
            // was left out to keep the context bounded. The creator's remedy differs — shorten a guide section,
            // or name fewer documents — so the two stay separate messages.
            BrandContextOmission.ExcerptOverBudget => (
                AiWarningKind.Limitation,
                ExcerptOverBudget,
                "Some writing samples were left out to keep the brand context within its size limit."),

            BrandContextOmission.GuideSectionOverBudget => (
                AiWarningKind.Limitation,
                GuideSectionOverBudget,
                "Some brand style guide sections were left out to keep the brand context within its size limit."),

            _ => throw new ArgumentOutOfRangeException(
                nameof(omission), omission, "A brand-context omission has no notice."),
        };
}
