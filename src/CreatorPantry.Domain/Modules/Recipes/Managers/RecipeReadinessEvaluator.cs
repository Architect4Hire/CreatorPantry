using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Decides whether a recipe is ready, by rule, from facts that have already been read.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No model decides readiness</strong> (TESTRUN-004). Every verdict here comes out of a comparison
/// against a column or a count. A model may have written some of the material being judged — a proposal's
/// warnings are among the facts — but what those facts mean for approval is settled by the code below, the same
/// way every time.
/// </para>
/// <para>
/// <strong>Pure, and deliberately so.</strong> No I/O, no clock, no workspace context, no randomness: the same
/// facts always produce the same findings, which is what lets one test cover one rule and what makes a result a
/// creator disputes reproducible. Everything it needs was gathered by the layers above.
/// </para>
/// <para>
/// <strong>It writes nothing</strong> — there is nothing here that could, which is the strongest form the
/// restriction takes.
/// </para>
/// <para>
/// <strong>One rule, one method.</strong> The methods are trivial in isolation and that is the point: a rule
/// whose condition is one expression can be read against its catalogue entry and confirmed, and its failure
/// message and evidence sit next to the condition that produced them rather than in a formatter somewhere else.
/// </para>
/// </remarks>
public static class RecipeReadinessEvaluator
{
    /// <summary>
    /// Runs every enabled rule against the facts and assembles the result.
    /// </summary>
    /// <param name="facts">This module's own facts about the recipe, already read.</param>
    /// <param name="external">
    /// What the other modules said. <see cref="RecipeReadinessExternalFacts.None"/> is the honest value when
    /// neither was asked.
    /// </param>
    /// <param name="options">
    /// Severity overrides and disabled rules, or <c>null</c> for the catalogue's own defaults.
    /// </param>
    public static RecipeReadinessServiceModel Evaluate(
        RecipeReadinessFacts facts,
        RecipeReadinessExternalFacts external,
        RecipeReadinessOptions? options = null)
    {
        var findings = new List<RecipeReadinessFinding>(RecipeReadinessCatalogue.Rules.Count);

        foreach (var rule in RecipeReadinessCatalogue.Rules)
        {
            if (options?.DisabledRules.Contains(rule.Id) is true)
            {
                // Absent rather than satisfied: a rule that did not run has not passed. The id is reported on the
                // result so the shorter checklist is explained.
                continue;
            }

            var outcome = Apply(rule.Id, facts, external);

            var severity = options is not null && options.Severities.TryGetValue(rule.Id, out var configured)
                ? configured
                : rule.DefaultSeverity;

            findings.Add(new RecipeReadinessFinding(
                rule.Id,
                Status(outcome, severity),
                rule.Summary,
                outcome.Detail,
                outcome.Evidence,
                SeverityOverridden: severity != rule.DefaultSeverity));
        }

        return new RecipeReadinessServiceModel(
            facts.RecipeId,
            facts.EvaluatedVersionId,
            facts.EvaluatedVersionNumber,
            RecipeConcurrencyToken.From(facts.RecipeRowVersion),
            RecipeReadinessCatalogue.Version,
            findings.Any(finding => finding.Status is RecipeReadinessStatus.Blocker),
            findings.Count(finding => finding.Status is RecipeReadinessStatus.Blocker),
            findings.Count(finding => finding.Status is RecipeReadinessStatus.Recommendation),
            findings,
            [.. DisabledIn(options)],
            [.. UnknownIn(options)]);
    }

    /// <summary>
    /// What one rule found, before its configured severity turns it into a status.
    /// </summary>
    /// <remarks>
    /// Separating the two is what lets a rule's condition be written once and reported at whichever severity is in
    /// force: a rule says "unmet, and here is why", and the severity decides whether that is a bar or advice.
    /// </remarks>
    private readonly record struct RuleOutcome(
        bool Met,
        bool Applies,
        string? Detail,
        IReadOnlyList<RecipeReadinessEvidence> Evidence)
    {
        internal static RuleOutcome Satisfied() => new(true, true, null, []);

        internal static RuleOutcome NotApplicable() => new(true, false, null, []);

        internal static RuleOutcome Unmet(string detail, params RecipeReadinessEvidence[] evidence) =>
            new(false, true, detail, evidence);
    }

    private static RecipeReadinessStatus Status(RuleOutcome outcome, RecipeReadinessSeverity severity) => outcome switch
    {
        { Applies: false } => RecipeReadinessStatus.NotApplicable,
        { Met: true } => RecipeReadinessStatus.Satisfied,
        _ when severity is RecipeReadinessSeverity.Blocker => RecipeReadinessStatus.Blocker,
        _ => RecipeReadinessStatus.Recommendation,
    };

    private static IEnumerable<string> DisabledIn(RecipeReadinessOptions? options) =>
        options is null
            ? []
            : options.DisabledRules.Where(RecipeReadinessCatalogue.ById.ContainsKey).Order(StringComparer.Ordinal);

    /// <summary>
    /// Ids configuration named that the catalogue does not have, from either setting.
    /// </summary>
    /// <remarks>
    /// Both collections are checked, because a typo is as likely in a severity override as in a disable — and an
    /// override on an id that does not exist is the quieter of the two failures, since nothing about the result
    /// looks unusual.
    /// </remarks>
    private static IEnumerable<string> UnknownIn(RecipeReadinessOptions? options) =>
        options is null
            ? []
            : options.DisabledRules.Concat(options.Severities.Keys)
                .Where(id => !RecipeReadinessCatalogue.ById.ContainsKey(id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

    private static RuleOutcome Apply(
        string ruleId,
        RecipeReadinessFacts facts,
        RecipeReadinessExternalFacts external) => ruleId switch
    {
        RecipeReadinessCatalogue.IngredientsPresent => IngredientsPresent(facts),
        RecipeReadinessCatalogue.InstructionsPresent => InstructionsPresent(facts),
        RecipeReadinessCatalogue.YieldStated => YieldStated(facts),
        RecipeReadinessCatalogue.TimeStated => TimeStated(facts),
        RecipeReadinessCatalogue.DescriptionPresent => DescriptionPresent(facts),
        RecipeReadinessCatalogue.AttributionPresent => AttributionPresent(facts),
        RecipeReadinessCatalogue.IngredientsAmbiguous => IngredientsAmbiguous(facts),
        RecipeReadinessCatalogue.IngredientsUnrecognized => IngredientsUnrecognized(facts),
        RecipeReadinessCatalogue.AiSafetyCautionOutstanding => AiSafetyCautionOutstanding(external),
        RecipeReadinessCatalogue.AiWarningOutstanding => AiWarningOutstanding(external),
        RecipeReadinessCatalogue.AiChangesPending => AiChangesPending(external),
        RecipeReadinessCatalogue.AllergensTraitUnreviewed => AllergensTraitUnreviewed(external),
        RecipeReadinessCatalogue.TestingCurrentVersionUntested => TestingCurrentVersionUntested(facts),
        RecipeReadinessCatalogue.TestingLastOutcomeFailed => TestingLastOutcomeFailed(facts),
        RecipeReadinessCatalogue.TestingBlockingIssueOutstanding => TestingIssues(facts, blocking: true),
        RecipeReadinessCatalogue.TestingIssueOutstanding => TestingIssues(facts, blocking: false),
        RecipeReadinessCatalogue.MediaHeroMissing => MediaHeroPresent(facts),
        RecipeReadinessCatalogue.PublicationCuisineMissing =>
            Named(facts.CuisineId, nameof(RecipeReadinessFacts.CuisineId), "cuisine", facts),
        RecipeReadinessCatalogue.PublicationCourseMissing =>
            Named(facts.CourseId, nameof(RecipeReadinessFacts.CourseId), "course", facts),
        RecipeReadinessCatalogue.PublicationTagsMissing => TagsPresent(facts),

        // Unreachable through the catalogue, which is the only thing that calls this. It throws rather than
        // returning satisfied, because a rule listed and not implemented must fail loudly in a test rather than
        // silently report a recipe as having cleared something nothing checked.
        _ => throw new ArgumentOutOfRangeException(nameof(ruleId), ruleId, "Unknown readiness rule."),
    };

    // ---- Required fields ----

    private static RuleOutcome IngredientsPresent(RecipeReadinessFacts facts) =>
        facts.IngredientLines.Count > 0
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                "This recipe has no ingredient lines.",
                new RecipeReadinessEvidence(RecipeReadinessEvidenceKind.Recipe, facts.RecipeId));

    private static RuleOutcome InstructionsPresent(RecipeReadinessFacts facts) =>
        facts.InstructionStepCount > 0
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                "This recipe has no instruction steps.",
                new RecipeReadinessEvidence(RecipeReadinessEvidenceKind.Recipe, facts.RecipeId));

    /// <summary>
    /// A yield is stated by any one of its three separable facts.
    /// </summary>
    /// <remarks>
    /// The three are deliberately separate on the recipe — the creator's wording, the measured batch, and the
    /// serving count — and any one of them answers "what does this make". Requiring a particular one would refuse
    /// a recipe that records only how many it serves, which recipes.md names as a legitimate shape.
    /// </remarks>
    private static RuleOutcome YieldStated(RecipeReadinessFacts facts) =>
        !string.IsNullOrWhiteSpace(facts.YieldText)
        || (facts.YieldQuantity is not null && facts.YieldUnitId is not null)
        || facts.ServingCount is not null
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                "This recipe does not say what it makes — no yield in words, no measured batch, and no serving "
                    + "count.",
                new RecipeReadinessEvidence(
                    RecipeReadinessEvidenceKind.Recipe, facts.RecipeId, nameof(RecipeReadinessFacts.YieldText)));

    /// <summary>
    /// A time is stated by the total or by any one of the parts.
    /// </summary>
    /// <remarks>
    /// It does not add the parts up and does not compare them to the total. Total time is stored independently of
    /// prep, cook and rest, and a real kitchen's total is legitimately smaller than their sum where steps overlap
    /// (recipes.md) — so a rule that reconciled them would report a defect in arithmetic nobody performed.
    /// </remarks>
    private static RuleOutcome TimeStated(RecipeReadinessFacts facts) =>
        facts.TotalTimeMinutes is not null
        || facts.PrepTimeMinutes is not null
        || facts.CookTimeMinutes is not null
        || facts.RestTimeMinutes is not null
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                "This recipe states no times — no total, and no prep, cook or rest.",
                new RecipeReadinessEvidence(
                    RecipeReadinessEvidenceKind.Recipe,
                    facts.RecipeId,
                    nameof(RecipeReadinessFacts.TotalTimeMinutes)));

    private static RuleOutcome DescriptionPresent(RecipeReadinessFacts facts) =>
        !string.IsNullOrWhiteSpace(facts.Description)
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                "This recipe has no description, so a listing or a share card has nothing to show beneath its "
                    + "title.",
                new RecipeReadinessEvidence(
                    RecipeReadinessEvidenceKind.Recipe, facts.RecipeId, nameof(RecipeReadinessFacts.Description)));

    /// <summary>
    /// A recipe citing a source URL must credit that source in words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Inapplicable rather than satisfied when there is no source URL</strong>, which is the case
    /// <see cref="RecipeReadinessStatus.NotApplicable"/> exists for: an original recipe has not passed an
    /// attribution check, there was nothing to check.
    /// </para>
    /// <para>
    /// A blocker because it is a rights question rather than a polish one. A URL is a machine's reference; the
    /// words are what a reader sees, and publishing someone else's recipe with the first and not the second is the
    /// failure external.md's ownership rules are about.
    /// </para>
    /// </remarks>
    private static RuleOutcome AttributionPresent(RecipeReadinessFacts facts) =>
        string.IsNullOrWhiteSpace(facts.SourceUrl)
            ? RuleOutcome.NotApplicable()
            : !string.IsNullOrWhiteSpace(facts.AttributionText)
                ? RuleOutcome.Satisfied()
                : RuleOutcome.Unmet(
                    "This recipe cites a source URL but credits no source in words.",
                    new RecipeReadinessEvidence(
                        RecipeReadinessEvidenceKind.Recipe,
                        facts.RecipeId,
                        nameof(RecipeReadinessFacts.AttributionText)));

    // ---- Ingredient resolution ----

    /// <summary>
    /// No ingredient line is ambiguous between two or more vocabulary entries.
    /// </summary>
    /// <remarks>
    /// A blocker where <see cref="IngredientsUnrecognized"/> is advice, and the difference is whether the creator
    /// can act: an ambiguous line has candidates to choose between, so the bar is clearable. Evidence names each
    /// line by the creator's own text, never by a normalized form.
    /// </remarks>
    private static RuleOutcome IngredientsAmbiguous(RecipeReadinessFacts facts) =>
        Lines(facts, IngredientMatchStatus.Ambiguous) is { Count: > 0 } ambiguous
            ? RuleOutcome.Unmet(
                Sentence(ambiguous.Count, "ingredient line matches", "ingredient lines match")
                    + " more than one entry in the shared vocabulary, so which one it means is undecided.",
                [.. ambiguous.Select(EvidenceFor)])
            : RuleOutcome.Satisfied();

    /// <summary>
    /// Every ingredient line is recognised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Advice, not a bar: a line the vocabulary does not know has nothing to resolve to, so blocking on it would
    /// be a requirement the creator cannot clear. That is the distinction
    /// <see cref="RecipeIngredientReviewFilter"/> already draws between the two.
    /// </para>
    /// <para>
    /// <see cref="IngredientMatchStatus.NotAttempted"/> counts with <see cref="IngredientMatchStatus.NoMatch"/>
    /// here. Both mean the line carries no reference, and the difference between them — whether anybody has tried —
    /// is about the pipeline rather than about the recipe.
    /// </para>
    /// </remarks>
    private static RuleOutcome IngredientsUnrecognized(RecipeReadinessFacts facts) =>
        Lines(facts, IngredientMatchStatus.NoMatch, IngredientMatchStatus.NotAttempted) is { Count: > 0 } unresolved
            ? RuleOutcome.Unmet(
                Sentence(unresolved.Count, "ingredient line is", "ingredient lines are")
                    + " not recognised, so those lines carry no dietary or allergen references.",
                [.. unresolved.Select(EvidenceFor)])
            : RuleOutcome.Satisfied();

    private static List<RecipeReadinessIngredientLine> Lines(
        RecipeReadinessFacts facts,
        params IngredientMatchStatus[] statuses) =>
        [.. facts.IngredientLines.Where(line => statuses.Contains(line.MatchStatus))];

    private static RecipeReadinessEvidence EvidenceFor(RecipeReadinessIngredientLine line) =>
        new(RecipeReadinessEvidenceKind.RecipeIngredientLine,
            line.Id,
            nameof(RecipeReadinessIngredientLine.MatchStatus),
            line.DisplayText);

    // ---- Outstanding AI work ----

    /// <summary>
    /// The warning kinds that are about the food rather than about the writing.
    /// </summary>
    /// <remarks>
    /// These two block and the rest advise, because these two are the ones a creator must have answered before
    /// publishing: a caution about a cooking step or a safety consideration is the kind of thing ai.md requires be
    /// surfaced rather than absorbed. An <see cref="AiWarningKind.Assumption"/> or a
    /// <see cref="AiWarningKind.Rationale"/> is the model explaining itself.
    /// </remarks>
    private static readonly AiWarningKind[] SafetyRelatedWarnings =
        [AiWarningKind.SafetyCaution, AiWarningKind.CulinaryCaution];

    private static RuleOutcome AiSafetyCautionOutstanding(RecipeReadinessExternalFacts external) =>
        Warnings(external, safetyRelated: true) is { Count: > 0 } cautions
            ? RuleOutcome.Unmet(
                Sentence(cautions.Count, "safety or culinary caution from an AI proposal is", "such cautions are")
                    + " still unanswered.",
                [.. cautions.Select(EvidenceFor)])
            : RuleOutcome.Satisfied();

    private static RuleOutcome AiWarningOutstanding(RecipeReadinessExternalFacts external) =>
        Warnings(external, safetyRelated: false) is { Count: > 0 } warnings
            ? RuleOutcome.Unmet(
                Sentence(warnings.Count, "AI warning is", "AI warnings are") + " still unanswered.",
                [.. warnings.Select(EvidenceFor)])
            : RuleOutcome.Satisfied();

    private static RuleOutcome AiChangesPending(RecipeReadinessExternalFacts external) =>
        external.Ai.PendingChangeCount is 0
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                Sentence(external.Ai.PendingChangeCount, "AI-proposed change is", "AI-proposed changes are")
                    + " still undecided.",
                // From the proposal ids rather than from the warnings: a proposal can hold undecided changes and
                // raise no warning, and a finding that could not name its own records would fail the
                // explainability this evaluation is built around.
                [.. external.Ai.ProposalIds.Select(id =>
                    new RecipeReadinessEvidence(RecipeReadinessEvidenceKind.AiProposal, id))]);

    private static List<AiOutstandingWarningServiceModel> Warnings(
        RecipeReadinessExternalFacts external,
        bool safetyRelated) =>
        [.. external.Ai.Warnings.Where(warning =>
            SafetyRelatedWarnings.Contains(warning.Kind) == safetyRelated)];

    /// <summary>
    /// Evidence for a warning: the proposal, and the model's own message as the label.
    /// </summary>
    /// <remarks>
    /// The message is quoted rather than summarized, because a caution paraphrased is a caution changed. It is
    /// text a model wrote about the creator's own recipe, which is why it travels in a response and never into a
    /// log (ai.md).
    /// </remarks>
    private static RecipeReadinessEvidence EvidenceFor(AiOutstandingWarningServiceModel warning) =>
        new(RecipeReadinessEvidenceKind.AiProposal, warning.AiProposalId, warning.Kind.ToString(), warning.Message);

    // ---- Allergen records ----

    /// <summary>
    /// Every recognised ingredient has reviewed allergen records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This reports on records and never on food.</strong> The detail says nobody has finished checking;
    /// it does not say the recipe contains an allergen, and — the direction recipes.md is most explicit about — it
    /// never says one is absent. A clear result here means the records are complete, not that the dish is safe for
    /// anyone.
    /// </para>
    /// <para>
    /// A recommendation for that reason, not because the question is unimportant. A blocker phrased around
    /// allergens would read as a safety clearance, and this evaluation is not one and cannot be made into one.
    /// </para>
    /// <para>
    /// It names no allergen, because the read behind it does not: see
    /// <see cref="IngredientAllergenReviewServiceModel"/>.
    /// </para>
    /// </remarks>
    private static RuleOutcome AllergensTraitUnreviewed(RecipeReadinessExternalFacts external) =>
        external.AllergenReviewGaps is { Count: > 0 } gaps
            ? RuleOutcome.Unmet(
                Sentence(gaps.Count, "recognised ingredient has", "recognised ingredients have")
                    + " incomplete allergen records, so this recipe's allergen picture cannot be stated from "
                    + "reviewed sources. This is a statement about the records and not about the food.",
                [.. gaps.Select(gap => new RecipeReadinessEvidence(
                    RecipeReadinessEvidenceKind.Ingredient,
                    gap.IngredientId,
                    gap.State.ToString(),
                    gap.CanonicalName.Length is 0 ? null : gap.CanonicalName))])
            : RuleOutcome.Satisfied();

    // ---- Test coverage ----

    /// <summary>
    /// Somebody has cooked the version as it now stands.
    /// </summary>
    /// <remarks>
    /// Against the exact current version, not "has ever been tested". A test is evidence about the words that were
    /// cooked, so an edit since the last bake means the current words are untested — which is precisely what makes
    /// this the rule the test kitchen exists to serve.
    /// </remarks>
    private static RuleOutcome TestingCurrentVersionUntested(RecipeReadinessFacts facts) =>
        facts.TestedCurrentVersion
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                facts.EvaluatedVersionNumber is { } number
                    ? $"Nobody has recorded a test of version {number}, which is the recipe as it now stands."
                    : "This recipe has no version to have tested.",
                new RecipeReadinessEvidence(
                    RecipeReadinessEvidenceKind.RecipeVersion,
                    facts.EvaluatedVersionId,
                    Label: facts.EvaluatedVersionNumber?.ToString()));

    /// <summary>
    /// The most recent test of the current version did not fail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The most recent, not any.</strong> A recipe that failed and was fixed has a failure in its history
    /// and is not failing; judging the whole history would mean a bad early bake could never be cleared.
    /// </para>
    /// <para>
    /// Inapplicable when the version has no test at all, because
    /// <see cref="TestingCurrentVersionUntested"/> already says that and one gap should not produce two blockers
    /// saying the same thing in different words.
    /// </para>
    /// <para>
    /// It reads the tester's own verdict and never derives one from the issues: a tester may record a success with
    /// three things to fix, and the model keeps their word for it.
    /// </para>
    /// </remarks>
    private static RuleOutcome TestingLastOutcomeFailed(RecipeReadinessFacts facts) => facts.LatestTestOutcome switch
    {
        null => RuleOutcome.NotApplicable(),
        TestRunOutcome.Failed => RuleOutcome.Unmet(
            "The most recent test of this version was recorded as a failure.",
            new RecipeReadinessEvidence(
                RecipeReadinessEvidenceKind.RecipeTestRun,
                facts.LatestTestRunId,
                nameof(RecipeReadinessFacts.LatestTestOutcome))),
        _ => RuleOutcome.Satisfied(),
    };

    /// <summary>
    /// No issue of the given weight found by a test of the current version is still unresolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One method for two rules because the condition is one condition read at two weights — and because writing
    /// it twice would be two places for "unresolved" to come to mean two things. "Unresolved" is the absence of a
    /// resolution row, which is the single meaning <c>TestIssue</c> establishes.
    /// </para>
    /// <para>
    /// The split is at <see cref="TestIssueSeverity.Blocking"/>: the tester said this stops the recipe, so the
    /// evaluation agrees with them rather than re-judging it.
    /// </para>
    /// </remarks>
    private static RuleOutcome TestingIssues(RecipeReadinessFacts facts, bool blocking)
    {
        var matching = facts.OpenIssues
            .Where(issue => (issue.Severity is TestIssueSeverity.Blocking) == blocking)
            .ToList();

        if (matching.Count is 0)
        {
            return RuleOutcome.Satisfied();
        }

        var weight = blocking ? "blocking issue" : "issue";
        var plural = blocking ? "blocking issues" : "issues";

        return RuleOutcome.Unmet(
            Sentence(matching.Count, $"{weight} found by a test of this version is", $"{plural} found by tests of this version are")
                + " still unresolved.",
            [.. matching.Select(issue => new RecipeReadinessEvidence(
                RecipeReadinessEvidenceKind.TestIssue,
                issue.Id,
                nameof(RecipeReadinessOpenIssue.Severity),
                issue.Title))]);
    }

    // ---- Media ----

    /// <summary>
    /// The recipe links a hero image.
    /// </summary>
    /// <remarks>
    /// The link and no more. <c>RecipeAssetLink.MediaAssetId</c> has no foreign key yet, so nothing here can
    /// confirm the asset exists, that it is an image, or that it has alt text — and stating otherwise would be the
    /// kind of unverified claim media.md rules out. When the media library lands, those become rules of their own.
    /// </remarks>
    private static RuleOutcome MediaHeroPresent(RecipeReadinessFacts facts) =>
        facts.HasHeroAsset
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                "This recipe links no hero image.",
                new RecipeReadinessEvidence(
                    RecipeReadinessEvidenceKind.RecipeAssetLink, facts.HeroAssetLinkId ?? facts.RecipeId));

    // ---- Publication-facing metadata ----

    private static RuleOutcome Named(Guid? id, string fieldName, string what, RecipeReadinessFacts facts) =>
        id is not null
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                $"This recipe names no {what}.",
                new RecipeReadinessEvidence(RecipeReadinessEvidenceKind.Recipe, facts.RecipeId, fieldName));

    private static RuleOutcome TagsPresent(RecipeReadinessFacts facts) =>
        facts.TagCount > 0
            ? RuleOutcome.Satisfied()
            : RuleOutcome.Unmet(
                "This recipe carries none of the workspace's tags.",
                new RecipeReadinessEvidence(
                    RecipeReadinessEvidenceKind.Recipe, facts.RecipeId, nameof(RecipeReadinessFacts.TagCount)));

    /// <summary>
    /// "One ingredient line matches" / "Three ingredient lines match" — a count and the clause that agrees with it.
    /// </summary>
    /// <remarks>
    /// Here rather than left to a client, because a finding's detail is a sentence a creator reads and a client
    /// assembling it would be reimplementing these rules to phrase them. Both forms are passed in rather than
    /// derived, since English does not let a verb be pluralized by rule.
    /// </remarks>
    private static string Sentence(int count, string singular, string plural) =>
        count is 1 ? $"One {singular}" : $"{count} {plural}";
}
