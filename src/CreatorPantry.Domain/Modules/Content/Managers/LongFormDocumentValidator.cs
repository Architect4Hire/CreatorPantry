namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>How much a finding matters. Stored nowhere; a result is recomputed from the revision it describes.</summary>
public enum LongFormFindingSeverity
{
    /// <summary>The plan's shape is wrong. A plan with one cannot be approved.</summary>
    Failure = 1,

    /// <summary>
    /// A body or the document is over its ceiling. Stored as written and never trimmed; never refuses a save,
    /// and acceptance asks the creator to confirm it first.
    /// </summary>
    OverCeiling = 2,

    /// <summary>Guidance was missed. Shown, and nothing else.</summary>
    Warning = 3,
}

/// <summary>Stable codes for <see cref="LongFormFinding.Code"/>.</summary>
public static class LongFormFindingCodes
{
    public const string NoSections = "content.long_form.sections.none";
    public const string DuplicateSectionId = "content.long_form.section.duplicate_id";
    public const string UnknownRole = "content.long_form.role.unknown";
    public const string RoleNotAllowed = "content.long_form.role.not_allowed";
    public const string RoleRepeated = "content.long_form.role.repeated";
    public const string HeadingBlank = "content.long_form.heading.blank";
    public const string HeadingTooLong = "content.long_form.heading.too_long";
    public const string IntentBlank = "content.long_form.intent.blank";
    public const string IntentNotOneLine = "content.long_form.intent.not_one_line";
    public const string IntentTooLong = "content.long_form.intent.too_long";

    /// <summary>A newsletter item names no source reference (LF.4.1).</summary>
    public const string ItemSourceMissing = "content.long_form.item.source_missing";

    /// <summary>A newsletter item names a reference its revision did not pin.</summary>
    public const string ItemSourceUnknown = "content.long_form.item.source_unknown";

    /// <summary>A second item names a reference another item already covers.</summary>
    public const string ItemSourceRepeated = "content.long_form.item.source_repeated";

    /// <summary>About the plan: a pinned reference has no item, so the items and the references disagree.</summary>
    public const string ItemsSourceUncovered = "content.long_form.items.source_uncovered";

    /// <summary>A section that is not an item names a source reference.</summary>
    public const string SectionSourceNotAllowed = "content.long_form.section.source_not_allowed";
    public const string SectionOverCeiling = "content.long_form.section.over_ceiling";
    public const string DocumentOverCeiling = "content.long_form.document.over_ceiling";
    public const string WordsUnderTarget = "content.long_form.words.under_target";
    public const string WordsOverTarget = "content.long_form.words.over_target";
}

/// <summary>One thing the validator found.</summary>
/// <param name="SectionId">The section it is about, or null when it is about the document as a whole.</param>
public sealed record LongFormFinding(string Code, LongFormFindingSeverity Severity, Guid? SectionId);

/// <summary>How one section measured.</summary>
/// <param name="CharacterCount">The body's length as the policy counts it, or null while it is unwritten.</param>
public sealed record LongFormSectionMeasure(Guid SectionId, int? CharacterCount, bool OverCeiling);

/// <summary>Where a document stands against its kind's target length.</summary>
public enum LongFormWordTargetStatus
{
    /// <summary>A planned section has no body yet, so the document is not short — it is unfinished.</summary>
    NotAssessed = 0,
    Within = 1,
    Under = 2,
    Over = 3,
}

/// <summary>A document scored against <see cref="LongFormPolicy"/>.</summary>
/// <param name="PolicyVersion">Which rules produced this result.</param>
/// <param name="WordCount">Words across the bodies written so far.</param>
public sealed record LongFormValidationResult(
    string PolicyVersion,
    IReadOnlyList<LongFormFinding> Findings,
    IReadOnlyList<LongFormSectionMeasure> Sections,
    int CharacterCount,
    int WordCount,
    LongFormWordRange TargetWords,
    LongFormWordTargetStatus WordTarget)
{
    /// <summary>True when nothing about the plan's shape is wrong. Ceilings and targets do not enter into it.</summary>
    public bool IsPlanValid => Findings.All(finding => finding.Severity != LongFormFindingSeverity.Failure);

    public bool HasOverCeiling => Findings.Any(finding => finding.Severity == LongFormFindingSeverity.OverCeiling);
}

/// <summary>
/// Scores a long-form document against <see cref="LongFormPolicy"/> (LF.1.3). Pure: it reads a document and
/// returns findings, and changes nothing — an over-long body is reported, never trimmed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A newsletter's items are bound to its revision's pinned references, one to one (LF.4.1, B-35).</strong>
/// Each item names one reference, each pinned reference has exactly one item, and nothing but an item names a
/// reference at all. Where an item sits among the other sections is free; which reference it covers is not.
/// </para>
/// <para>
/// <strong>Bound to the pins, not to the creative context as it stands today.</strong> The pins are the
/// context's recipe references as the revision recorded them, so the answer is a fact about the immutable
/// revision: a reference the context later drops cannot turn an approved plan into an invalid one. Whether a
/// reference still resolves is a different question, answered where the draft is read.
/// </para>
/// </remarks>
public static class LongFormDocumentValidator
{
    /// <param name="boundReferenceIds">
    /// The creative-context references the revision pinned. Null when the caller holds no pins and only the
    /// shape and the lengths are wanted — the binding is then not assessed, which is not the same as it holding.
    /// </param>
    public static LongFormValidationResult Validate(
        LongFormKind kind, LongFormDocument document, IReadOnlyCollection<Guid>? boundReferenceIds = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var target = LongFormPolicy.TargetWords(kind);
        var sections = document.Sections ?? [];
        var findings = new List<LongFormFinding>();
        var measures = new List<LongFormSectionMeasure>(sections.Count);

        if (sections.Count == 0)
        {
            findings.Add(new(LongFormFindingCodes.NoSections, LongFormFindingSeverity.Failure, null));
        }

        var seenIds = new HashSet<Guid>();
        var bound = boundReferenceIds?.ToHashSet();
        var covered = new HashSet<Guid>();
        var roleCounts = new Dictionary<LongFormSectionRole, int>();
        var characters = 0;
        var words = 0;
        var everySectionWritten = sections.Count > 0;

        foreach (var section in sections)
        {
            if (!seenIds.Add(section.Id))
            {
                Fail(LongFormFindingCodes.DuplicateSectionId, section);
            }

            CheckRole(kind, section, roleCounts, findings);
            CheckHeadingAndIntent(section, findings);

            if (bound is not null)
            {
                CheckSource(kind, section, bound, covered, findings);
            }

            if (string.IsNullOrWhiteSpace(section.Body))
            {
                everySectionWritten = false;
                measures.Add(new(section.Id, null, false));

                continue;
            }

            var count = LongFormWordCounter.CountCharacters(section.Body);
            var over = count > LongFormPolicy.SectionMaxCharacters;

            if (over)
            {
                findings.Add(new(LongFormFindingCodes.SectionOverCeiling, LongFormFindingSeverity.OverCeiling, section.Id));
            }

            characters += count;
            words += LongFormWordCounter.CountWords(section.Body);
            measures.Add(new(section.Id, count, over));
        }

        // Named once, about the plan: no one section is to blame for a reference that has none.
        if (bound is not null && kind is LongFormKind.NewsletterIssue && !bound.IsSubsetOf(covered))
        {
            findings.Add(new(LongFormFindingCodes.ItemsSourceUncovered, LongFormFindingSeverity.Failure, null));
        }

        if (characters > LongFormPolicy.DocumentMaxCharacters)
        {
            findings.Add(new(LongFormFindingCodes.DocumentOverCeiling, LongFormFindingSeverity.OverCeiling, null));
        }

        var wordTarget = !everySectionWritten ? LongFormWordTargetStatus.NotAssessed
            : words < target.Min ? LongFormWordTargetStatus.Under
            : words > target.Max ? LongFormWordTargetStatus.Over
            : LongFormWordTargetStatus.Within;

        if (wordTarget == LongFormWordTargetStatus.Under)
        {
            findings.Add(new(LongFormFindingCodes.WordsUnderTarget, LongFormFindingSeverity.Warning, null));
        }
        else if (wordTarget == LongFormWordTargetStatus.Over)
        {
            findings.Add(new(LongFormFindingCodes.WordsOverTarget, LongFormFindingSeverity.Warning, null));
        }

        return new(LongFormPolicy.Version, findings, measures, characters, words, target, wordTarget);

        void Fail(string code, LongFormSection section) =>
            findings.Add(new(code, LongFormFindingSeverity.Failure, section.Id));
    }

    private static void CheckRole(
        LongFormKind kind,
        LongFormSection section,
        Dictionary<LongFormSectionRole, int> roleCounts,
        List<LongFormFinding> findings)
    {
        if (!Enum.IsDefined(section.Role))
        {
            findings.Add(new(LongFormFindingCodes.UnknownRole, LongFormFindingSeverity.Failure, section.Id));

            return;
        }

        var rule = LongFormPolicy.FindRole(kind, section.Role);

        if (rule is null)
        {
            findings.Add(new(LongFormFindingCodes.RoleNotAllowed, LongFormFindingSeverity.Failure, section.Id));

            return;
        }

        var count = roleCounts[section.Role] = roleCounts.GetValueOrDefault(section.Role) + 1;

        // Reported on each section past the allowance, so the first use of a role is never the one named.
        if (rule.MaxOccurrences is { } max && count > max)
        {
            findings.Add(new(LongFormFindingCodes.RoleRepeated, LongFormFindingSeverity.Failure, section.Id));
        }
    }

    private static void CheckSource(
        LongFormKind kind,
        LongFormSection section,
        HashSet<Guid> bound,
        HashSet<Guid> covered,
        List<LongFormFinding> findings)
    {
        if (section.Role is not LongFormSectionRole.Item)
        {
            if (section.SourceReferenceId is not null)
            {
                findings.Add(new(LongFormFindingCodes.SectionSourceNotAllowed, LongFormFindingSeverity.Failure, section.Id));
            }

            return;
        }

        // An item in a kind that has none is already a role failure, and says all there is to say.
        if (kind is not LongFormKind.NewsletterIssue)
        {
            return;
        }

        var code = section.SourceReferenceId switch
        {
            null => LongFormFindingCodes.ItemSourceMissing,
            { } source when !bound.Contains(source) => LongFormFindingCodes.ItemSourceUnknown,

            // Reported on the second item to name a reference, so the first is never the one named.
            { } source when !covered.Add(source) => LongFormFindingCodes.ItemSourceRepeated,
            _ => null,
        };

        if (code is not null)
        {
            findings.Add(new(code, LongFormFindingSeverity.Failure, section.Id));
        }
    }

    private static void CheckHeadingAndIntent(LongFormSection section, List<LongFormFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(section.Heading))
        {
            findings.Add(new(LongFormFindingCodes.HeadingBlank, LongFormFindingSeverity.Failure, section.Id));
        }
        else if (LongFormWordCounter.CountCharacters(section.Heading) > LongFormPolicy.HeadingMaxCharacters)
        {
            findings.Add(new(LongFormFindingCodes.HeadingTooLong, LongFormFindingSeverity.Failure, section.Id));
        }

        if (string.IsNullOrWhiteSpace(section.Intent))
        {
            findings.Add(new(LongFormFindingCodes.IntentBlank, LongFormFindingSeverity.Failure, section.Id));

            return;
        }

        if (section.Intent.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            findings.Add(new(LongFormFindingCodes.IntentNotOneLine, LongFormFindingSeverity.Failure, section.Id));
        }

        if (LongFormWordCounter.CountCharacters(section.Intent) > LongFormPolicy.IntentMaxCharacters)
        {
            findings.Add(new(LongFormFindingCodes.IntentTooLong, LongFormFindingSeverity.Failure, section.Id));
        }
    }
}
