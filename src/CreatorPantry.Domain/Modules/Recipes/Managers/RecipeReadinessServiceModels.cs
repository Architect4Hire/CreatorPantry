namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What one rule concluded about one recipe, and why.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A finding exists for every rule that ran</strong>, satisfied ones included, because the thing this
/// feeds is a checklist: a creator needs to see what they have cleared as much as what they have not. Disabled
/// rules are the exception and are absent entirely — see <see cref="RecipeReadinessOptions"/>.
/// </para>
/// <para>
/// <strong><see cref="Detail"/> names the specifics and <see cref="Evidence"/> points at them.</strong> The
/// first is a sentence a creator reads; the second is what a screen links to. Neither is optional on an unmet
/// rule, which is how TESTRUN-004's "explain exactly which record/field caused each result" is kept.
/// </para>
/// </remarks>
/// <param name="RuleId">
/// The stable id from <see cref="RecipeReadinessCatalogue"/>. What a client branches on; never a localized
/// string.
/// </param>
/// <param name="Detail">
/// What is true of <em>this</em> recipe, or <c>null</c> for a satisfied or inapplicable rule, where the rule's
/// own summary already says everything. Quotes the creator's own words where the evidence has any.
/// </param>
/// <param name="SeverityOverridden">
/// <c>true</c> when configuration reported this rule at a severity other than the catalogue's default. Published
/// so that a relaxed bar is visible rather than looking like a rule that was never strict.
/// </param>
public sealed record RecipeReadinessFinding(
    string RuleId,
    RecipeReadinessStatus Status,
    string Summary,
    string? Detail,
    IReadOnlyList<RecipeReadinessEvidence> Evidence,
    bool SeverityOverridden);

/// <summary>
/// The result of one readiness evaluation of one recipe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing was written to produce this.</strong> No state changed, no audit row was staged, no cache
/// entry was set — TESTRUN-004 is a read, and a readiness evaluation that recorded its own verdict would create a
/// second source of truth that goes stale the moment the recipe is edited.
/// </para>
/// <para>
/// <strong>This is not a status and must not be stored as one.</strong> It describes the recipe at the moment it
/// was read. The recipe's editorial state is <c>Status</c>, and its lifecycle state is 10.6's business; a screen
/// that cached <see cref="HasBlockers"/> as a badge would be showing a verdict about content that has since
/// changed.
/// </para>
/// <para>
/// <strong>It says nothing about food safety, allergens, nutrition or dietary suitability.</strong> The nearest
/// rule, <see cref="RecipeReadinessCatalogue.AllergensTraitUnreviewed"/>, reports how complete the allergen
/// <em>records</em> are and never what is in the dish — and no absence of findings here means a recipe is safe
/// for anyone. A surface presenting this as a safety clearance would be making the claim recipes.md forbids.
/// </para>
/// </remarks>
/// <param name="EvaluatedVersionNumber">
/// The version the evaluated content belongs to, or <c>null</c> for a recipe with no version. Published so a
/// result can be cited: "cleared at version 4" is a statement; "cleared" is not.
/// </param>
/// <param name="ConcurrencyToken">
/// The recipe's token at the moment of evaluation, opaque to a client. It is what 10.6's approval quotes to prove
/// it is approving the recipe that was evaluated rather than one edited since.
/// </param>
/// <param name="RuleSetVersion">
/// <see cref="RecipeReadinessCatalogue.Version"/>, so a result stays interpretable after the catalogue moves.
/// </param>
/// <param name="DisabledRuleIds">
/// Rules configuration switched off, and therefore absent from <see cref="Findings"/>. Published because a rule
/// that did not run has not passed, and a shorter checklist with no explanation would read as one.
/// </param>
/// <param name="UnknownConfiguredRuleIds">
/// Rule ids configuration named that the catalogue does not have — a typo, or an override left behind by an
/// upgrade. Reported rather than thrown, so a stale configuration file cannot stop the API starting, and reported
/// rather than swallowed, so it is discoverable.
/// </param>
public sealed record RecipeReadinessServiceModel(
    Guid RecipeId,
    Guid? EvaluatedVersionId,
    int? EvaluatedVersionNumber,
    string ConcurrencyToken,
    string RuleSetVersion,
    bool HasBlockers,
    int BlockerCount,
    int RecommendationCount,
    IReadOnlyList<RecipeReadinessFinding> Findings,
    IReadOnlyList<string> DisabledRuleIds,
    IReadOnlyList<string> UnknownConfiguredRuleIds);
