namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The configurable half of the readiness catalogue: which rules run, and how severely each unmet one is
/// reported.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deployment-wide, not per workspace.</strong> Storing a creator's own thresholds would mean a
/// workspace-owned configuration entity, its own write seam, and its own audit trail — a decision of its own
/// rather than a property bag. This is the operator's dial: a release can soften a rule it has just added, or
/// turn one off while the data it reads is still being backfilled, without a deployment of new code.
/// </para>
/// <para>
/// <strong>An override changes how an unmet rule is reported, never whether the rule is right.</strong> A
/// blocker relaxed to a recommendation still appears, still names its evidence, and the finding still says the
/// severity was overridden — <see cref="RecipeReadinessFinding.SeverityOverridden"/>. Silently downgrading a bar
/// would make an approval look like it cleared something it did not.
/// </para>
/// <para>
/// <strong>Disabling drops the rule from the result entirely</strong> rather than reporting it as satisfied. A
/// rule that did not run has not passed, and <see cref="RecipeReadinessStatus.Satisfied"/> would be a claim
/// nothing checked. The evaluation reports which ids were disabled so the gap is visible.
/// </para>
/// <para>
/// Unknown ids are ignored rather than fatal, which is the behaviour a configuration file wants across an upgrade
/// that removed a rule: a stale override should not stop the API starting. The evaluation reports the ids it did
/// not recognise, so a typo is discoverable rather than silent.
/// </para>
/// </remarks>
public sealed class RecipeReadinessOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "Recipes:Readiness";

    /// <summary>
    /// Severity overrides by rule id, replacing <see cref="RecipeReadinessRule.DefaultSeverity"/>.
    /// </summary>
    /// <remarks>
    /// Ordinal comparison, because a rule id is an identifier rather than prose — <c>recipe.yield.stated</c> and
    /// <c>Recipe.Yield.Stated</c> are not the same id, and accepting the second would mean configuration could
    /// name a rule in a spelling no client would ever branch on.
    /// </remarks>
    public Dictionary<string, RecipeReadinessSeverity> Severities { get; } = new(StringComparer.Ordinal);

    /// <summary>Rule ids that do not run at all. See the type's remarks for why this is not "always satisfied".</summary>
    public HashSet<string> DisabledRules { get; } = new(StringComparer.Ordinal);
}
