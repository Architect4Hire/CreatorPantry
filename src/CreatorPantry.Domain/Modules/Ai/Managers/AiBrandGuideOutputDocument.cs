namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The structured answer 11A.17's brand-guide analysis is held to. The document is also the schema:
/// <see cref="AiBrandGuideOutputSchema.Json"/> exports it for the prompt.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No field for a person.</strong> There is nowhere to put a writer's name, a publication, a handle or
/// any attribute of a human being, so "write in the style of <em>X</em>" and an inferred trait both have nowhere
/// to live — the same way <see cref="AiSeoPackageOutputDocument"/> keeps metrics out by having no numeric field.
/// A name a model writes into prose anyway is looked for by <see cref="AiBrandGuideClaimScanner"/>.
/// </para>
/// <para>
/// <strong>No field for a quotation.</strong> Guidance describes how the creator writes; it does not reproduce
/// what they wrote. There is no excerpt, quote or example-passage field, and the scanner refuses a body that
/// copies a long run of a cited passage anyway.
/// </para>
/// <para>
/// <strong>Evidence is required, not optional.</strong> Every section and rule states what it rests on and
/// carries the citations that back it, so thin support is visible in the shape of the answer instead of being
/// absent from it. A section claiming <see cref="AiBrandGuideEvidence.Sources"/> with no citation is refused.
/// </para>
/// <para>
/// Nothing is repaired. An empty answer is valid — a selection that supports no guidance is a result, and
/// <see cref="Uncertainties"/> is where it says so.
/// </para>
/// </remarks>
public sealed record AiBrandGuideOutputDocument
{
    public required string SchemaVersion { get; init; }

    public IReadOnlyList<AiBrandGuideSection> Sections { get; init; } = [];

    public IReadOnlyList<AiBrandGuideRule> Rules { get; init; } = [];

    public IReadOnlyList<AiBrandGuideConflict> Conflicts { get; init; } = [];

    public IReadOnlyList<AiBrandGuideUncertainty> Uncertainties { get; init; } = [];

    public IReadOnlyList<AiBrandGuideOutputWarning> Warnings { get; init; } = [];
}

/// <summary>
/// Which part of the brand's writing a piece of guidance is about.
/// </summary>
/// <remarks>
/// Deliberately the capability's own vocabulary rather than <c>BrandStyleGuideSectionKey</c>'s. A proposal is
/// not a guide version, so binding the two enums together would make a model's answer shape a stored guide's
/// — and the guide's keys include ones a proposal has no business producing. The handler maps a dimension onto
/// a row the creator reads; it never writes a guide section.
/// </remarks>
public enum AiBrandGuideDimension
{
    Unspecified = 0,

    /// <summary>The persona behind the words: who the brand sounds like.</summary>
    Voice = 1,

    /// <summary>The feeling the words carry, which may shift by situation.</summary>
    Tone = 2,

    /// <summary>The stance toward the reader — peer, teacher, host.</summary>
    Tenor = 3,

    /// <summary>Sentence shape, rhythm, formatting habits, punctuation.</summary>
    Style = 4,

    /// <summary>Vocabulary, spelling conventions, words used and avoided.</summary>
    Language = 5,

    /// <summary>Guidance for one named channel. The only dimension that carries a channel key.</summary>
    Channel = 6,

    /// <summary>How a long-form post is built: headnotes, intros, structure.</summary>
    Blog = 7,

    /// <summary>How short social copy reads.</summary>
    Social = 8,

    /// <summary>
    /// Direction for photography and generated imagery: framing, light, props, styling.
    /// </summary>
    /// <remarks>
    /// Guidance a creator or an image prompt follows, never a description of a particular picture. Nothing here
    /// has looked at any image, so this dimension states intent and never reports what an existing asset shows
    /// (media.md).
    /// </remarks>
    Visual = 9,
}

/// <summary>What one piece of guidance rests on.</summary>
/// <remarks>
/// Stated rather than inferred from whether citations are present, because the two can disagree in a way a
/// creator must see: an answer claiming its guidance comes from the sources while citing none is refused rather
/// than quietly re-labelled.
/// </remarks>
public enum AiBrandGuideEvidence
{
    Unspecified = 0,

    /// <summary>The creator's own answers on the guide. Needs no citation; the guide is the source.</summary>
    Questionnaire = 1,

    /// <summary>The selected source passages. Requires at least one citation.</summary>
    Sources = 2,

    /// <summary>Both. Requires at least one citation.</summary>
    Both = 3,

    /// <summary>
    /// Neither — a reasonable default the model is offering with nothing behind it.
    /// </summary>
    /// <remarks>
    /// Permitted, and the honest answer where a creator asked about a dimension their material says nothing
    /// about. The handler attaches an <see cref="AiWarningKind.Assumption"/> warning to every section that
    /// claims it, so the creator is never shown an unsupported suggestion that looks like a finding.
    /// </remarks>
    None = 4,
}

/// <summary>One passage this answer used, named by the row it came from.</summary>
/// <remarks>
/// <see cref="PassageId"/> is a <c>BrandSourceChunk</c> row id the request offered. The validator refuses any
/// other, which is what makes a citation a fact rather than a formatting convention — a model cannot invent
/// one, and one from another workspace was never offered in the first place.
/// </remarks>
public sealed record AiBrandGuideCitation
{
    public required Guid PassageId { get; init; }
}

/// <summary>One dimension's proposed guidance.</summary>
public sealed record AiBrandGuideSection
{
    public required AiBrandGuideDimension Dimension { get; init; }

    /// <summary>
    /// Required for <see cref="AiBrandGuideDimension.Channel"/> and refused for every other dimension.
    /// </summary>
    /// <remarks>
    /// An opaque key from the channel catalogue, never a provider name: channel constraints live in channel
    /// profiles and adapters (content.md). The validator checks it against the keys the request offered.
    /// </remarks>
    public string? ChannelKey { get; init; }

    public required string Body { get; init; }

    public required AiBrandGuideEvidence Evidence { get; init; }

    public IReadOnlyList<AiBrandGuideCitation> Citations { get; init; } = [];
}

/// <summary>One thing the brand always or never does.</summary>
public sealed record AiBrandGuideRule
{
    public required AiBrandGuideRuleKind Kind { get; init; }

    public required string Text { get; init; }

    public required AiBrandGuideEvidence Evidence { get; init; }

    public IReadOnlyList<AiBrandGuideCitation> Citations { get; init; } = [];
}

/// <summary>Whether a rule is something to do or something to avoid.</summary>
/// <remarks>
/// The capability's own enum for the reason <see cref="AiBrandGuideDimension"/> gives: a proposal's vocabulary
/// is not a stored guide's, even where the two currently agree.
/// </remarks>
public enum AiBrandGuideRuleKind
{
    Unspecified = 0,

    Do = 1,

    Dont = 2,
}

/// <summary>
/// Two pieces of the creator's own material that disagree, and where each side is.
/// </summary>
/// <remarks>
/// <strong>Required to cite both sides.</strong> A conflict with one citation is an opinion about a passage; a
/// conflict with two is a finding a creator can check. The validator enforces the floor, so "do not hide
/// contradictory evidence" is a property of the shape rather than an instruction the prompt hopes was followed.
/// </remarks>
public sealed record AiBrandGuideConflict
{
    public AiBrandGuideDimension? Dimension { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<AiBrandGuideCitation> Citations { get; init; } = [];
}

/// <summary>Something the answer could not settle, in the model's own words.</summary>
public sealed record AiBrandGuideUncertainty
{
    public AiBrandGuideDimension? Dimension { get; init; }

    public required string Summary { get; init; }
}

/// <summary>A caution the model attached to its own answer, optionally naming a dimension.</summary>
public sealed record AiBrandGuideOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public AiBrandGuideDimension? Dimension { get; init; }

    public required string Message { get; init; }
}

/// <summary>The field names on a brand-guide proposal's rows, beside the item's own text.</summary>
public static class AiBrandGuideFields
{
    public const string Dimension = "dimension";

    public const string ChannelKey = "channelKey";

    public const string Evidence = "evidence";

    /// <summary>The cited passage ids, comma-separated, in the order the answer gave them.</summary>
    public const string Citations = "citations";

    public const string RuleKind = "ruleKind";

    /// <summary>What kind of item the row is: a section, a rule, a conflict or an uncertainty.</summary>
    public const string ItemKind = "itemKind";
}

/// <summary>Which of the four item kinds one proposed row is.</summary>
public static class AiBrandGuideItemKinds
{
    public const string Section = "section";

    public const string Rule = "rule";

    public const string Conflict = "conflict";

    public const string Uncertainty = "uncertainty";
}

/// <summary>The keys the request writes into <c>AiOperation.TaskInputsJson</c> and the handler reads back.</summary>
public static class AiBrandGuideProposalInputs
{
    /// <summary>The guide this proposal is about, as a <c>D</c>-formatted Guid.</summary>
    public const string GuideId = "guideId";

    /// <summary>
    /// The guide version number the creator's answers were read from, pinned at request time.
    /// </summary>
    /// <remarks>
    /// The handler refuses to run if the guide has moved past it, for the reason the SEO package refuses a
    /// changed rule set: a proposal explained by answers that have since been rewritten is unexplainable.
    /// </remarks>
    public const string GuideVersionNumber = "guideVersionNumber";

    /// <summary>
    /// The selected source document versions, as <c>{documentId:N}:{versionNumber}</c> joined by commas.
    /// </summary>
    public const string SourceVersions = "sourceVersions";

    /// <summary>The dimensions asked for, comma-separated by name.</summary>
    public const string Dimensions = "dimensions";

    /// <summary>The channel keys offered for <see cref="AiBrandGuideDimension.Channel"/>, comma-separated.</summary>
    public const string ChannelKeys = "channelKeys";
}
