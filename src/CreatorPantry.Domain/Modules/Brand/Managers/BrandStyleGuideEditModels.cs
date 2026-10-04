using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The body of <c>POST .../brand-style-guides/{guideId}/versions</c>: what a creator changed about their guide,
/// which this module writes as one further immutable version.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A submitted change document, not a replacement guide.</strong> An omitted collection is untouched, so
/// a request that names one section says nothing about the others and cannot discard them. That is what makes an
/// editor able to save one part of a long form, and what keeps two people editing different sections from
/// overwriting each other's words by sending their own copy of the whole guide.
/// </para>
/// <para>
/// There is no field for a workspace, a guide or a version to write: the workspace comes from the resolved
/// context, the guide is a route segment, and the version number is one past the working version — assigned by
/// the server, never asked for.
/// </para>
/// <para>
/// <strong>What the guide <em>is</em> — its name, its purpose, whether it is archived — is not here.</strong>
/// Those live on the guide row rather than on any version of it, and changing them says nothing different about
/// how the brand writes. A route for them quotes the guide's <c>concurrencyToken</c>; this one quotes
/// <see cref="ExpectedWorkingVersionNumber"/>, because writing a version never touches that row and so never
/// moves its token.
/// </para>
/// </remarks>
public sealed record SaveBrandStyleGuideVersionViewModel
{
    /// <summary>
    /// The version the creator was editing, as the guide read reported it. Required.
    /// </summary>
    /// <remarks>
    /// The optimistic guard, and the reason this route cannot silently rebase an edit. It is checked against the
    /// guide as it actually stands inside the writing transaction, so an edit that lands between the read and
    /// the save is refused rather than applied to words the creator never saw.
    /// </remarks>
    [Description("The working version number this edit was made against, from the guide read. A mismatch is refused.")]
    public int? ExpectedWorkingVersionNumber { get; init; }

    /// <summary>The creator's own note on why they changed it. Optional, and stored on the new version.</summary>
    [Description("Why this change was made, in the creator's own words. Stored on the new version.")]
    public string? ChangeReason { get; init; }

    /// <summary>
    /// The sections to set or clear. A section the request does not name is left exactly as it was.
    /// </summary>
    [Description("Sections to set or clear. Omit the field to leave every section alone.")]
    public IReadOnlyList<BrandStyleGuideSectionEditViewModel?>? Sections { get; init; }

    /// <summary>
    /// The do and don't rules, in full, or absent to leave them alone.
    /// </summary>
    [Description("The complete ordered rule list, replacing the one the working version holds. Omit to keep it.")]
    public BrandStyleGuideRulesEditViewModel? Rules { get; init; }

    /// <summary>The citations to add and to drop. Each names an exact source document version.</summary>
    [Description("Source document versions to cite and to stop citing. Omit the field to leave citations alone.")]
    public BrandStyleGuideSourceEditViewModel? SourceDocuments { get; init; }
}

/// <summary>One section to set or to clear, identified the way a version identifies it.</summary>
/// <remarks>
/// <strong>A listed section with no body is a deletion, not a blank one.</strong> That is the difference from
/// creation, where a blank answer is simply no answer: here the creator has emptied a part of their own guide,
/// and the only way to say so is to name the part and send nothing for it. Which also means a section is only
/// ever touched by being named — there is no shape of this request that clears something by omission.
/// </remarks>
public sealed record BrandStyleGuideSectionEditViewModel
{
    [Description("Which section this is. Required.")]
    public BrandStyleGuideSectionKey? SectionKey { get; init; }

    /// <summary>Required for <c>ChannelVariant</c>, and refused for every other key.</summary>
    [Description("The channel, for a channel variant only. It identifies which variant, so a variant needs it even to be cleared.")]
    public string? ChannelKey { get; init; }

    /// <summary>The section's text, stored exactly as typed. Null or blank clears the section.</summary>
    [Description("The section's text, stored verbatim. Null or blank clears the section.")]
    public string? Body { get; init; }
}

/// <summary>
/// The guide's do and don't rules, in full.
/// </summary>
/// <remarks>
/// Whole-list rather than per-rule, because a rule has no identity beyond its own text: there is nothing stable
/// to address one by, and the order is the creator's. <see cref="Items"/> is required when this object is
/// present, so clearing every rule is something a request says — <c>"rules": {}</c> is refused rather than read
/// as "delete them all".
/// </remarks>
public sealed record BrandStyleGuideRulesEditViewModel
{
    [Description("The complete ordered list. An empty array clears every rule; the field itself is required here.")]
    public IReadOnlyList<BrandStyleGuideRuleEditViewModel?>? Items { get; init; }
}

public sealed record BrandStyleGuideRuleEditViewModel
{
    [Description("Do or Dont.")]
    public BrandStyleGuideRuleKind? Kind { get; init; }

    [Description("The rule, stored verbatim. A blank rule is refused rather than dropped.")]
    public string? Text { get; init; }
}

/// <summary>
/// Which citations to add and which to drop, each by document and exact version number.
/// </summary>
/// <remarks>
/// Symmetrical, and by exact version on both sides, because a version may legitimately cite two versions of one
/// document — so "stop citing document D" would be ambiguous where "stop citing D v1" is not. Re-pinning a
/// citation to a document's newer version is therefore one <see cref="Uncite"/> and one <see cref="Cite"/> in
/// the same request, which is also the only shape in which the creator has said they meant to move it.
/// </remarks>
public sealed record BrandStyleGuideSourceEditViewModel
{
    [Description("Source document versions to start citing. Already-cited versions are not duplicated.")]
    public IReadOnlyList<BrandStyleGuideSourceInput?>? Cite { get; init; }

    [Description("Source document versions to stop citing. One the version does not cite is not an error.")]
    public IReadOnlyList<BrandStyleGuideSourceInput?>? Uncite { get; init; }
}

/// <summary>
/// Shape validation for <see cref="SaveBrandStyleGuideVersionViewModel"/>.
/// </summary>
/// <remarks>
/// Shape only, as the module's other validators are. Whether the guide exists, whether it is archived, which
/// version is the working one, whether the result exceeds one of the guide's ceilings, and whether a cited
/// document version can still be used are all facts about the workspace's data, and backend.md keeps those in
/// Business.
/// </remarks>
public sealed class SaveBrandStyleGuideVersionViewModelValidator
    : AbstractValidator<SaveBrandStyleGuideVersionViewModel>
{
    public SaveBrandStyleGuideVersionViewModelValidator()
    {
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in BrandStyleGuideEditInput.Failures(model))
            {
                context.AddFailure(field, message);
            }
        });
    }
}

/// <summary>One section the request named: its identity, and the text to store or nothing to clear it.</summary>
/// <param name="Body">The text, verbatim, or <c>null</c> to remove the section.</param>
public sealed record BrandStyleGuideSectionEdit(
    BrandStyleGuideSectionKey SectionKey, string? ChannelKey, string? Body);

/// <summary>
/// A creator's submitted change to a guide, after blanks were resolved into what they mean: what to set, what to
/// clear, and what to cite.
/// </summary>
/// <param name="Rules">
/// The replacement rule list, or <c>null</c> when the request did not speak about rules at all. The distinction
/// matters: an empty list clears them, and null leaves them.
/// </param>
public sealed record BrandStyleGuideEditDraft(
    int ExpectedWorkingVersionNumber,
    string? ChangeReason,
    IReadOnlyList<BrandStyleGuideSectionEdit> Sections,
    IReadOnlyList<BrandStyleGuideRuleServiceModel>? Rules,
    IReadOnlyList<BrandStyleGuideSourceServiceModel> Cite,
    IReadOnlyList<BrandStyleGuideSourceServiceModel> Uncite);

/// <summary>
/// Shape checks for a submitted edit, and the one place its blanks are resolved — so what is validated and what
/// is applied cannot disagree about which section was being cleared.
/// </summary>
internal static class BrandStyleGuideEditInput
{
    public static IEnumerable<(string Field, string Message)> Failures(SaveBrandStyleGuideVersionViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.ExpectedWorkingVersionNumber is not { } expected || expected < 1)
        {
            yield return (nameof(model.ExpectedWorkingVersionNumber),
                "Name the version you are editing, as the guide read reported it.");
        }

        if (BrandProfileInputChecks.Normalize(model.ChangeReason) is { } reason
            && reason.Length > BrandPolicy.ReasonMaxLength)
        {
            yield return (nameof(model.ChangeReason),
                $"A change reason can be at most {BrandPolicy.ReasonMaxLength} characters.");
        }

        foreach (var failure in SectionFailures(model.Sections))
        {
            yield return failure;
        }

        foreach (var failure in RuleFailures(model.Rules))
        {
            yield return failure;
        }

        foreach (var failure in SourceFailures(model.SourceDocuments))
        {
            yield return failure;
        }

        if (IsEmpty(model))
        {
            // Told apart from the no-op Business answers. That one means the creator's words match what is
            // stored, which is a thing that happens and is reported as a success; this means the request asked
            // for nothing at all, which is a request worth refusing before it is measured against anything.
            yield return (nameof(model.Sections),
                "This request does not change anything: name a section, a rule list, or a citation.");
        }
    }

    /// <summary>What will be applied, with each blank resolved into the thing it means.</summary>
    /// <remarks>Assumes <see cref="Failures"/> found nothing.</remarks>
    public static BrandStyleGuideEditDraft Compose(SaveBrandStyleGuideVersionViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var sections = new List<BrandStyleGuideSectionEdit>();

        foreach (var section in model.Sections ?? [])
        {
            if (section is not { SectionKey: { } key })
            {
                continue;
            }

            sections.Add(new BrandStyleGuideSectionEdit(
                key,
                key is BrandStyleGuideSectionKey.ChannelVariant
                    ? BrandProfileInputChecks.Normalize(section.ChannelKey)
                    : null,

                // Verbatim where there is text, and null where there is not: a section nobody wrote anything
                // into is the one being removed, and trimming here would quietly rewrite the one being kept.
                BrandStyleGuideInput.Has(section.Body) ? section.Body : null));
        }

        return new BrandStyleGuideEditDraft(
            model.ExpectedWorkingVersionNumber!.Value,
            BrandProfileInputChecks.Normalize(model.ChangeReason),
            sections,
            model.Rules is null
                ? null
                : [
                    .. (model.Rules.Items ?? [])
                        .Where(rule => rule is { Kind: not null } && BrandStyleGuideInput.Has(rule.Text))
                        .Select(rule => new BrandStyleGuideRuleServiceModel(rule!.Kind!.Value, rule.Text!)),
                ],
            Sources(model.SourceDocuments?.Cite),
            Sources(model.SourceDocuments?.Uncite));
    }

    /// <summary>True when the request asks for nothing: no section, no rule list, no citation either way.</summary>
    private static bool IsEmpty(SaveBrandStyleGuideVersionViewModel model) =>
        (model.Sections is null || model.Sections.Count == 0)
        && model.Rules is null
        && (model.SourceDocuments is null
            || ((model.SourceDocuments.Cite is null || model.SourceDocuments.Cite.Count == 0)
                && (model.SourceDocuments.Uncite is null || model.SourceDocuments.Uncite.Count == 0)));

    private static IReadOnlyList<BrandStyleGuideSourceServiceModel> Sources(
        IReadOnlyList<BrandStyleGuideSourceInput?>? sources) =>
        [
            .. (sources ?? [])
                .Where(source => source is { DocumentId: not null, VersionNumber: not null })
                .Select(source => new BrandStyleGuideSourceServiceModel(
                    source!.DocumentId!.Value, source.VersionNumber!.Value)),
        ];

    private static IEnumerable<(string Field, string Message)> SectionFailures(
        IReadOnlyList<BrandStyleGuideSectionEditViewModel?>? sections)
    {
        var named = new HashSet<(BrandStyleGuideSectionKey, string)>();

        for (var index = 0; index < (sections?.Count ?? 0); index++)
        {
            var path = $"Sections[{index}]";
            var section = sections![index];

            if (section is null)
            {
                yield return (path, "Name the section to set or clear.");
                continue;
            }

            if (section.SectionKey is not { } key || !Enum.IsDefined(key))
            {
                yield return ($"{path}.SectionKey", "Choose which section this is.");
                continue;
            }

            if (BrandStyleGuideInput.Has(section.Body)
                && section.Body!.Length > BrandPolicy.StyleGuideSectionBodyMaxLength)
            {
                yield return ($"{path}.Body",
                    $"A section can be at most {BrandPolicy.StyleGuideSectionBodyMaxLength} characters.");
            }

            var channel = BrandProfileInputChecks.Normalize(section.ChannelKey) ?? string.Empty;

            if (key is BrandStyleGuideSectionKey.ChannelVariant)
            {
                // Required even to clear one: the channel is half of which section this is, and a variant
                // without it names every variant or none.
                if (channel.Length == 0 || !BrandProfileInputChecks.IsChannelKey(channel))
                {
                    yield return ($"{path}.ChannelKey", "A channel variant needs a channel key, such as instagram.");
                    continue;
                }
            }
            else if (channel.Length > 0)
            {
                yield return ($"{path}.ChannelKey", "Only a channel variant names a channel.");
                continue;
            }

            if (!named.Add((key, channel)))
            {
                yield return ($"{path}.SectionKey", "This section is already given once in the request.");
            }
        }
    }

    private static IEnumerable<(string Field, string Message)> RuleFailures(
        BrandStyleGuideRulesEditViewModel? rules)
    {
        if (rules is null)
        {
            yield break;
        }

        if (rules.Items is null)
        {
            // Refused rather than read as an empty list: deleting every rule the creator wrote is a decision,
            // and an object that happens to carry no array is not a way to say it.
            yield return ($"{nameof(SaveBrandStyleGuideVersionViewModel.Rules)}.Items",
                "List the rules, or leave rules out of the request to keep the ones already there.");
            yield break;
        }

        var seen = new HashSet<(BrandStyleGuideRuleKind, string)>();
        var duplicated = false;

        for (var index = 0; index < rules.Items.Count; index++)
        {
            var path = $"{nameof(SaveBrandStyleGuideVersionViewModel.Rules)}.Items[{index}]";
            var rule = rules.Items[index];

            if (rule is null)
            {
                yield return (path, "Write the rule, or leave it out of the list.");
                continue;
            }

            if (rule.Kind is not { } kind || !Enum.IsDefined(kind))
            {
                yield return ($"{path}.Kind", "Choose whether this is a do or a don't.");
                continue;
            }

            if (!BrandStyleGuideInput.Has(rule.Text))
            {
                // Refused rather than dropped, unlike creation's questionnaire: this list is the rules, so a
                // blank entry in it would silently shorten what the creator believes they saved.
                yield return ($"{path}.Text", "A rule cannot be blank.");
                continue;
            }

            if (rule.Text!.Length > BrandPolicy.StyleGuideRuleTextMaxLength)
            {
                yield return ($"{path}.Text",
                    $"A rule can be at most {BrandPolicy.StyleGuideRuleTextMaxLength} characters.");
                continue;
            }

            if (!seen.Add((kind, rule.Text.Trim().ToUpperInvariant())) && !duplicated)
            {
                // Matched the way the create validator matches rules, so "the same rule twice" means the same
                // thing however it was written. Reported once: a list pasted twice would otherwise answer with
                // one failure per line.
                duplicated = true;
                yield return (path, "The same rule is listed more than once.");
            }
        }

        if (rules.Items.Count > BrandPolicy.MaxStyleGuideRules)
        {
            yield return ($"{nameof(SaveBrandStyleGuideVersionViewModel.Rules)}.Items",
                $"A guide can have at most {BrandPolicy.MaxStyleGuideRules} do and don't rules.");
        }
    }

    private static IEnumerable<(string Field, string Message)> SourceFailures(
        BrandStyleGuideSourceEditViewModel? sources)
    {
        if (sources is null)
        {
            yield break;
        }

        var cited = new HashSet<(Guid, int)>();
        var dropped = new HashSet<(Guid, int)>();

        foreach (var failure in Pointers(nameof(sources.Cite), sources.Cite, cited))
        {
            yield return failure;
        }

        foreach (var failure in Pointers(nameof(sources.Uncite), sources.Uncite, dropped))
        {
            yield return failure;
        }

        var both = cited.Intersect(dropped).ToList();

        if (both.Count > 0)
        {
            // A request cannot both start and stop citing the same version: the two orders of applying it give
            // different guides, so there is no reading of it that is safe to pick.
            yield return ($"{nameof(SaveBrandStyleGuideVersionViewModel.SourceDocuments)}.{nameof(sources.Cite)}",
                "This request both cites and stops citing the same source version.");
        }
    }

    private static IEnumerable<(string Field, string Message)> Pointers(
        string list, IReadOnlyList<BrandStyleGuideSourceInput?>? pointers, HashSet<(Guid, int)> seen)
    {
        for (var index = 0; index < (pointers?.Count ?? 0); index++)
        {
            var path = $"{nameof(SaveBrandStyleGuideVersionViewModel.SourceDocuments)}.{list}[{index}]";

            if (pointers![index] is not { DocumentId: { } documentId, VersionNumber: { } version }
                || documentId == Guid.Empty
                || version < 1)
            {
                yield return (path, "Name a source document and one of its version numbers.");
            }
            else if (!seen.Add((documentId, version)))
            {
                yield return (path, "This version is already listed once in the request.");
            }
        }
    }
}

/// <summary>
/// What saving a creator's edit did: the version it wrote, or that it wrote none, and what changed on the way.
/// </summary>
/// <param name="VersionId">
/// The new version, or <c>null</c> when nothing was written because the submitted edit said what the guide
/// already said.
/// </param>
/// <param name="VersionNumber">The new version's number, or <c>null</c> for that same no-op.</param>
/// <param name="ParentVersionNumber">The working version the edit was applied to.</param>
/// <param name="SectionsCleared">Named sections that were removed because the request sent no text for them.</param>
/// <param name="RulesAdded">
/// Rules in the submitted list that the working version did not hold, matched the way the create validator
/// matches them. Reordering alone adds and removes nothing, and still writes a version.
/// </param>
/// <param name="StaleSourceCount">
/// How many of the new version's citations the owning document has since replaced. Not a refusal — a citation is
/// pinned to the version it was written from, which is the honest record — but reported, because a version with
/// a stale citation cannot be activated until it is rewritten from current sources.
/// </param>
/// <remarks>
/// <strong>A null <paramref name="VersionId"/> is a success.</strong> An edit that changes nothing writes no
/// version, records no audit entry and reports the version that still stands, because a history full of
/// versions that say nothing new is harder to read than one without them.
/// </remarks>
public sealed record BrandStyleGuideVersionSavedServiceModel(
    Guid GuideId,
    Guid? VersionId,
    int? VersionNumber,
    int ParentVersionNumber,
    int SectionsAdded,
    int SectionsReplaced,
    int SectionsCleared,
    int RulesAdded,
    int RulesRemoved,
    int SourcesCited,
    int SourcesUncited,
    int SectionCount,
    int RuleCount,
    int SourceCount,
    int StaleSourceCount);
