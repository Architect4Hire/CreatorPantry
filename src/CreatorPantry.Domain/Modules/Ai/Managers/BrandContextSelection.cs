using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The deterministic rules behind a <see cref="BrandContextPackage"/>: which guidance a task gets, what it costs,
/// what is dropped when it costs too much, and the checksum that identifies the result.
/// </summary>
/// <remarks>
/// <para>
/// Pure — no clock, no context, no persistence, no facade. That is what lets every rule below be asserted
/// directly rather than through whichever assemblies a test happens to arrange, and it is why the assembler can
/// decide all of this before it has written anything.
/// </para>
/// <para>
/// <strong>Nothing here reads prose for meaning.</strong> Relevance is a table of section keys, cost is a
/// character count, and a conflict is a comparison of stored values. A rule that needed to understand the
/// creator's words would not belong in a type called deterministic.
/// </para>
/// </remarks>
public static class BrandContextSelection
{
    /// <summary>
    /// The sections every writing task is grounded on: how the brand sounds, whoever is reading.
    /// </summary>
    /// <remarks>
    /// <see cref="BrandStyleGuideSectionKey.UserNotes"/> is deliberately absent from every task. It is the
    /// creator's own scratch area on the guide — notes to themselves about the guide — and sending it as
    /// guidance would turn a reminder into an instruction.
    /// </remarks>
    private static readonly BrandStyleGuideSectionKey[] Voice =
    [
        BrandStyleGuideSectionKey.Voice,
        BrandStyleGuideSectionKey.Tone,
        BrandStyleGuideSectionKey.Tenor,
        BrandStyleGuideSectionKey.WritingStyle,
        BrandStyleGuideSectionKey.Audience,
        BrandStyleGuideSectionKey.PointOfView,
        BrandStyleGuideSectionKey.Vocabulary,
        BrandStyleGuideSectionKey.SentenceRhythm,
    ];

    /// <summary>What a long-form piece needs beyond the voice: how it is built and how it closes.</summary>
    private static readonly BrandStyleGuideSectionKey[] LongForm =
    [
        .. Voice,
        BrandStyleGuideSectionKey.Formatting,
        BrandStyleGuideSectionKey.Storytelling,
        BrandStyleGuideSectionKey.CallsToAction,
        BrandStyleGuideSectionKey.BlogGuidance,
    ];

    /// <summary>
    /// What a short post needs beyond the voice: how the brand asks for something, and its guidance for the
    /// places it posts (AF.6.3).
    /// </summary>
    /// <remarks>
    /// Both the social and the blog guidance, because one request writes for whichever channels the creator
    /// picked and a blog intro is one of them. No storytelling and no formatting: those shape a long piece, and
    /// a caption is not one.
    /// </remarks>
    private static readonly BrandStyleGuideSectionKey[] ShortForm =
    [
        .. Voice,
        BrandStyleGuideSectionKey.CallsToAction,
        BrandStyleGuideSectionKey.BlogGuidance,
        BrandStyleGuideSectionKey.SocialGuidance,
    ];

    /// <summary>Visual direction. No voice sections: an image prompt is not written in the brand's voice.</summary>
    private static readonly BrandStyleGuideSectionKey[] VisualOnly =
    [
        BrandStyleGuideSectionKey.VisualIdentity,
        BrandStyleGuideSectionKey.PhotographyDirection,
        BrandStyleGuideSectionKey.ImagePromptGuidance,
        BrandStyleGuideSectionKey.NegativeVisualGuidance,
    ];

    /// <summary>
    /// The voice <em>and</em> the look, for the one task that demonstrates both (11A.24).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The only entry in this table that unions a writing list with the visual one</strong>, and the
    /// reason is that a test drive writes three pieces in one pass: a blog introduction and a social caption,
    /// which are the brand's voice, and an image prompt, which is its look. Splitting it into two packages
    /// would mean two checksums, two provenance rows and two conflict lists for one thing a creator asked for
    /// once — and either package would have had to record a <see cref="AiTaskType"/> it was not assembled for.
    /// </para>
    /// <para>
    /// It is a union rather than a widening of either list: <see cref="BrandStyleGuideSectionKey.UserNotes"/>
    /// stays out, as it does everywhere, and no task that writes recipe facts gains a section because this one
    /// exists.
    /// </para>
    /// <para>
    /// <see cref="BrandStyleGuideSectionKey.SocialGuidance"/> appears here and in no other entry, because the
    /// social caption is the first sample any task writes. It is the guide's general social guidance and not a
    /// channel variant — see <see cref="SectionKeysFor"/> on why a test drive passes no channel.
    /// </para>
    /// </remarks>
    private static readonly BrandStyleGuideSectionKey[] VoiceAndLook =
    [
        .. LongForm,
        BrandStyleGuideSectionKey.SocialGuidance,
        .. VisualOnly,
    ];

    /// <summary>
    /// Which guide sections one task is grounded on, in section-key order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A table, not a default of "everything".</strong> Sending the whole guide to every capability is
    /// the stuffing 11A.19 forbids, and most of it would be irrelevant: an ingredient substitution does not need
    /// to know the brand's photography direction, and asking a model to hold guidance it cannot use makes the
    /// guidance it <em>can</em> use harder to follow.
    /// </para>
    /// <para>
    /// <strong>The tasks that get nothing get nothing on purpose.</strong>
    /// <see cref="AiTaskType.IngredientSubstitution"/>, <see cref="AiTaskType.RecipeReview"/> and
    /// <see cref="AiTaskType.RecipeRevision"/> produce or judge recipe <em>facts</em>; brand voice must not
    /// reach them, because style rules may not alter canonical recipe facts or safety (11A.20's own
    /// restriction). <see cref="AiTaskType.BrandGuideProposal"/> is absent because it is how a guide gets
    /// written — grounding it in the guide it is proposing would be circular.
    /// <see cref="AiTaskType.ProposalExplanation"/> explains an existing proposal rather than writing in the
    /// brand's name, and <see cref="AiTaskType.Diagnostic"/> is a health check.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<BrandStyleGuideSectionKey> SectionKeysFor(AiTaskType taskType) => taskType switch
    {
        // Long-form editorial: the full voice plus how a post is built.
        AiTaskType.EditorialPackage => LongForm,

        // Metadata written in the brand's voice, but not a narrative: no storytelling, no calls to action.
        AiTaskType.SeoPackage => Voice,

        // A pitch for a recipe that does not exist yet: it is the brand speaking, so voice applies.
        AiTaskType.RecipeConcepts => Voice,

        // A first draft carries the creator's own prose — headnote, description — so voice applies to that
        // prose. The recipe's facts come from the brief, not from here.
        AiTaskType.RecipeFirstDraft => Voice,

        // Image tasks: the brand's look, never its voice, so a style rule cannot reach recipe facts (11A.21).
        AiTaskType.PhotographyConcept or AiTaskType.ImagePrompt => VisualOnly,

        // The test drive writes prose and an image prompt in one pass, so it is the one task that gets both.
        // It passes no channel: one package grounds a blog introduction and a social caption together, and a
        // channel key would pull a social variant over the long-form guidance the introduction needs. The
        // caption therefore demonstrates the guide's general social guidance, which the screen states.
        AiTaskType.BrandStyleTestDrive => VoiceAndLook,

        // Posts are the brand speaking in public, so the voice applies. No channel is passed: one package
        // grounds every channel the request names, so each post follows the guide's general guidance rather
        // than a per-channel variant.
        AiTaskType.ChannelPosts => ShortForm,

        // Everything else: no brand context. Named above rather than left to a reader to work out.
        _ => [],
    };

    /// <summary>Whether a task is an image task, grounded in the brand's look rather than its voice.</summary>
    public static bool IsVisual(AiTaskType taskType) =>
        taskType is AiTaskType.PhotographyConcept or AiTaskType.ImagePrompt;

    /// <summary>
    /// Which source-document purposes ground a task: visual direction for image tasks, both for the test drive,
    /// writing purposes otherwise.
    /// </summary>
    /// <remarks>
    /// A switch rather than the two-way choice this was, because <see cref="AiTaskType.BrandStyleTestDrive"/>
    /// is neither an image task nor purely a writing one — it is grounded in both, for the reason
    /// <see cref="VoiceAndLook"/> gives. <see cref="IsVisual"/> stays false for it: it demonstrates the look
    /// but it generates no image, and the visual-reference rules that turn on <see cref="IsVisual"/> are about
    /// generating one.
    /// </remarks>
    public static IReadOnlyList<BrandSourcePurpose> PurposesFor(AiTaskType taskType) => taskType switch
    {
        AiTaskType.PhotographyConcept or AiTaskType.ImagePrompt => VisualPurposes,
        AiTaskType.BrandStyleTestDrive => WritingAndVisualPurposes,
        _ => WritingPurposes,
    };

    private static readonly BrandSourcePurpose[] VisualPurposes = [BrandSourcePurpose.VisualDirection];

    private static readonly BrandSourcePurpose[] WritingPurposes =
        [BrandSourcePurpose.Voice, BrandSourcePurpose.WritingStyle, BrandSourcePurpose.Background];

    private static readonly BrandSourcePurpose[] WritingAndVisualPurposes =
        [.. WritingPurposes, BrandSourcePurpose.VisualDirection];

    /// <summary>Whether a task is grounded in brand context at all.</summary>
    public static bool AppliesTo(AiTaskType taskType) => SectionKeysFor(taskType).Count > 0;

    /// <summary>Every section key any task can ask for, for a test that holds this table against the enum.</summary>
    public static IReadOnlySet<BrandStyleGuideSectionKey> AllSelectableKeys { get; } =
        Enum.GetValues<AiTaskType>()
            .SelectMany(SectionKeysFor)
            .ToHashSet();

    /// <summary>
    /// A deterministic estimate of what text costs in a prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An estimate, and labelled one everywhere it surfaces.</strong> Four characters per token is the
    /// rough English ratio; it is wrong for any particular string and right enough to budget with. The domain
    /// holds no tokenizer and should not: a real count depends on the provider's vocabulary, which would make
    /// this package's content vary by deployment, and the provider's actual count arrives afterwards through
    /// usage recording (<c>AccountAiUsageEntry</c>) where it is exact.
    /// </para>
    /// <para>
    /// Rounded up, and zero only for genuinely empty text, so a budget can never be spent on something counted
    /// as free.
    /// </para>
    /// </remarks>
    public const int CharactersPerToken = 4;

    /// <summary>The estimate for one string. <inheritdoc cref="CharactersPerToken" path="/remarks"/></summary>
    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + CharactersPerToken - 1) / CharactersPerToken;

    /// <summary>What a whole package is estimated to cost, counting every piece of text it carries.</summary>
    /// <remarks>
    /// Counts the creator's text and nothing else — not enum names, not ids, not the checksum. Those travel as
    /// structure a prompt builder renders however it likes, and attributing their cost here would make the
    /// number depend on a rendering this type does not control.
    /// </remarks>
    public static int EstimatePackageTokens(
        BrandContextProfile? profile,
        IEnumerable<BrandContextGuidance> guidance,
        IEnumerable<BrandContextRule> rules,
        IEnumerable<BrandContextExcerpt> excerpts,
        string? audience)
    {
        ArgumentNullException.ThrowIfNull(guidance);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(excerpts);

        return EstimateProfileTokens(profile)
            + EstimateTokens(audience)
            + guidance.Sum(item => EstimateTokens(item.Body))
            + rules.Sum(rule => EstimateTokens(rule.Text))
            + excerpts.Sum(excerpt => EstimateTokens(excerpt.Text));
    }

    /// <summary>The profile's own contribution: its text, not its keys.</summary>
    public static int EstimateProfileTokens(BrandContextProfile? profile) =>
        profile is null
            ? 0
            : EstimateTokens(profile.BrandName)
                + EstimateTokens(profile.ShortDescription)
                + EstimateTokens(profile.DefaultAudience)
                + profile.ChannelDefaults.Sum(EstimateTokens);

    /// <summary>
    /// Spends the budget in priority order and reports what had to be left out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The order is the policy.</strong> Profile facts and rules first because they are short and are
    /// the brand's hard constraints; then the guidance sections, in section-key order, which is the order a
    /// creator reads them; then excerpts, which are the longest and the most replaceable — an excerpt is
    /// evidence for a style the sections already state.
    /// </para>
    /// <para>
    /// <strong>Nothing is truncated.</strong> An item that will not fit is dropped whole: half an excerpt is not
    /// a citable passage and half a section is advice with its qualification cut off. Every drop becomes an
    /// omission, so a short package says it is short rather than looking complete.
    /// </para>
    /// <para>
    /// <strong>Rules and the profile are never dropped.</strong> If they alone exceeded the budget the package
    /// would be over it, and that is the honest outcome — a guide whose do-and-don't list cannot fit in a prompt
    /// is a product problem to surface, not one to paper over by silently discarding the brand's constraints.
    /// The caps on rule count and length (<c>BrandPolicy.MaxStyleGuideRules</c>, 50, at
    /// <c>StyleGuideRuleTextMaxLength</c>, 500) keep that unreachable in practice.
    /// </para>
    /// </remarks>
    /// <param name="budget">The ceiling, in estimated tokens.</param>
    public static BrandContextBudgetResult Spend(
        int budget,
        BrandContextProfile? profile,
        string? audience,
        IReadOnlyList<BrandContextGuidance> guidance,
        IReadOnlyList<BrandContextRule> rules,
        IReadOnlyList<BrandContextExcerpt> excerpts)
    {
        ArgumentNullException.ThrowIfNull(guidance);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(excerpts);

        var spent = EstimateProfileTokens(profile)
            + EstimateTokens(audience)
            + rules.Sum(rule => EstimateTokens(rule.Text));

        var keptGuidance = new List<BrandContextGuidance>(guidance.Count);
        var droppedGuidance = 0;

        foreach (var item in guidance)
        {
            var cost = EstimateTokens(item.Body);

            if (spent + cost > budget)
            {
                droppedGuidance++;
                continue;
            }

            spent += cost;
            keptGuidance.Add(item);
        }

        var keptExcerpts = new List<BrandContextExcerpt>(excerpts.Count);
        var droppedExcerpts = 0;

        foreach (var excerpt in excerpts)
        {
            var cost = EstimateTokens(excerpt.Text);

            if (spent + cost > budget)
            {
                droppedExcerpts++;
                continue;
            }

            spent += cost;
            keptExcerpts.Add(excerpt);
        }

        return new BrandContextBudgetResult(keptGuidance, keptExcerpts, droppedGuidance, droppedExcerpts, spent);
    }

    /// <summary>
    /// The canonical rendering a package's checksum is taken over, and the checksum itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It covers what was pinned and what was selected, and nothing else.</strong> The guide version,
    /// the profile revision, the task, channel and audience, every section key and body, every rule, and every
    /// <c>(document, version, passage)</c>. Not the assembly time, not the estimate, not the conflicts or
    /// omissions — those are consequences of the inputs, and including them would make the checksum change when
    /// only the reporting changed while leaving it unable to detect anything new.
    /// </para>
    /// <para>
    /// <strong>Fixed ordering and invariant culture throughout</strong>, so the same stored state hashes the
    /// same on any machine in any locale. Lengths are written before variable-length values so two different
    /// selections cannot render to one string — without that, a section body ending in a separator could forge
    /// the start of the next field.
    /// </para>
    /// </remarks>
    public static string Checksum(
        AiTaskType taskType,
        string? channelKey,
        string? audience,
        BrandContextProfile? profile,
        Guid? guideVersionId,
        int? guideVersionNumber,
        IReadOnlyList<BrandContextGuidance> guidance,
        IReadOnlyList<BrandContextRule> rules,
        IReadOnlyList<BrandContextExcerpt> excerpts)
    {
        ArgumentNullException.ThrowIfNull(guidance);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(excerpts);

        var canonical = new StringBuilder();

        void Field(string name, string? value)
        {
            canonical.Append(name).Append('=');
            canonical.Append(value is null ? "~" : value.Length.ToString(CultureInfo.InvariantCulture));
            canonical.Append(':').Append(value).Append('\n');
        }

        Field("task", taskType.ToString());
        Field("channel", channelKey);
        Field("audience", audience);
        Field("profileRevision", profile?.Revision.ToString(CultureInfo.InvariantCulture));
        Field("brandName", profile?.BrandName);
        Field("shortDescription", profile?.ShortDescription);
        Field("profileDefaultAudience", profile?.DefaultAudience);
        Field("locale", profile?.Locale);
        Field("channelDefaults", profile is null ? null : string.Join(',', profile.ChannelDefaults));
        Field("guideVersionId", guideVersionId?.ToString("N"));
        Field("guideVersionNumber", guideVersionNumber?.ToString(CultureInfo.InvariantCulture));

        foreach (var item in guidance)
        {
            Field($"section:{item.SectionKey}:{item.ChannelKey}", item.Body);
        }

        foreach (var rule in rules)
        {
            Field($"rule:{rule.Kind}", rule.Text);
        }

        foreach (var excerpt in excerpts)
        {
            // The passage id identifies the text, and the text travels anyway: a corrected extraction rewrites
            // the chunk under the same id, and a package grounded on the new words must not hash as the old.
            Field(
                $"excerpt:{excerpt.DocumentId:N}:{excerpt.VersionNumber.ToString(CultureInfo.InvariantCulture)}:{excerpt.PassageId:N}",
                excerpt.Text);
        }

        return "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}

/// <summary>What the budget allowed, and what it cost.</summary>
/// <param name="DroppedGuidance">Sections left out. Each becomes a <c>GuideSectionOverBudget</c> omission.</param>
/// <param name="DroppedExcerpts">Excerpts left out. Each becomes an <c>ExcerptOverBudget</c> omission.</param>
/// <param name="EstimatedTokens">
/// What the kept content is estimated to cost. May exceed the budget only when the profile and the rules alone
/// do — see <see cref="BrandContextSelection.Spend"/>.
/// </param>
public sealed record BrandContextBudgetResult(
    IReadOnlyList<BrandContextGuidance> Guidance,
    IReadOnlyList<BrandContextExcerpt> Excerpts,
    int DroppedGuidance,
    int DroppedExcerpts,
    int EstimatedTokens);
