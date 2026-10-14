using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>One defect in a workflow definition or capability: a stable code, where it is, and what is wrong.</summary>
/// <param name="Path">Where, e.g. <c>recipe.create@1/steps[ingredients]/completionRule</c>.</param>
public sealed record WorkflowDefinitionError(string Code, string Path, string Message)
{
    public override string ToString() => $"{Code} at {Path}: {Message}";
}

/// <summary>The codes <see cref="WorkflowDefinitionValidator"/> reports, one per kind of defect.</summary>
public static class WorkflowDefinitionErrorCodes
{
    public const string KeyInvalid = "workflow.key.invalid";
    public const string VersionInvalid = "workflow.version.invalid";
    public const string CopyMissing = "workflow.copy.missing";
    public const string CopyTooLong = "workflow.copy.too_long";
    public const string CopyJargon = "workflow.copy.jargon";
    public const string StepsEmpty = "workflow.steps.empty";
    public const string StepsNoneRequired = "workflow.steps.none_required";
    public const string StepKeyInvalid = "workflow.step.key.invalid";
    public const string StepKeyDuplicate = "workflow.step.key.duplicate";
    public const string StepRequirementInvalid = "workflow.step.requirement.invalid";
    public const string PrerequisiteNotAllowed = "workflow.prerequisite.not_allowed";
    public const string PrerequisiteStepNotEarlier = "workflow.prerequisite.step_not_earlier";
    public const string ResourceKindUnknown = "workflow.resource.kind_unknown";
    public const string ResourceNotYetLinked = "workflow.resource.not_yet_linked";
    public const string CapabilityUnknown = "workflow.capability.unknown";
    public const string CapabilityUnavailableForRequiredStep = "workflow.capability.unavailable_for_required_step";
    public const string CapabilityTooManyAlternatives = "workflow.capability.too_many_alternatives";
    public const string CapabilityDuplicate = "workflow.capability.duplicate";
    public const string CapabilityRouteInvalid = "workflow.capability.route_invalid";
    public const string CompletionAcknowledgedOnRequiredStep = "workflow.completion.acknowledged_on_required_step";
    public const string CompletionReadinessRulesEmpty = "workflow.completion.readiness_rules_empty";
    public const string CompletionReadinessRuleUnknown = "workflow.completion.readiness_rule_unknown";
    public const string CompletionStatusesEmpty = "workflow.completion.statuses_empty";
    public const string CompletionStatusUnknown = "workflow.completion.status_unknown";
    public const string SuggestionsTooMany = "workflow.suggestions.too_many";
    public const string SuggestionDuplicate = "workflow.suggestion.duplicate";
    public const string SuggestionSelf = "workflow.suggestion.self";
    public const string SuggestionUnknownWorkflow = "workflow.suggestion.unknown_workflow";
    public const string UnavailableNotBlocked = "workflow.unavailable.not_blocked";
    public const string StartsFromUnread = "workflow.starts_from.unread";
    public const string StartsFromDuplicate = "workflow.starts_from.duplicate";
    public const string PrerequisiteFactUnknown = "workflow.prerequisite.fact_unknown";
    public const string CompletionKindsEmpty = "workflow.completion.kinds_empty";
    public const string CompletionKindDuplicate = "workflow.completion.kind_duplicate";
    public const string CatalogueDuplicateVersion = "workflow.catalogue.duplicate_version";
    public const string CatalogueVersionGap = "workflow.catalogue.version_gap";
}

/// <summary>
/// Checks one <see cref="WorkflowDefinition"/>, or the capability registry, against the contract.
/// </summary>
/// <remarks>
/// <para>
/// Reports every defect rather than stopping at the first, each under a stable code, so a test can assert the
/// one it means and a reader of a failure sees all of them at once.
/// </para>
/// <para>
/// <strong>What it does not check</strong>: that a suggested workflow exists. That needs every definition at
/// once and is <see cref="WorkflowCatalogueValidator"/>'s. Nor does it decide anything about a recipe: the
/// readiness rule ids it accepts are checked against <see cref="RecipeReadinessCatalogue"/> to exist, and what
/// they mean stays the recipe module's.
/// </para>
/// </remarks>
public static partial class WorkflowDefinitionValidator
{
    public const int MaxKeyLength = 64;
    public const int MaxGoalLength = 60;
    public const int MaxTitleLength = 60;
    public const int MaxCapabilityNameLength = 40;
    public const int MaxDescriptionLength = 280;
    public const int MaxOutcomeLength = 280;
    public const int MaxHelpLength = 400;
    public const int MaxReasonLength = 200;
    public const int MaxAlternatives = 2;
    public const int MaxSuggestions = 3;

    /// <summary>
    /// Words a creator should never need to know (the easy-as-pie rule): how the product works inside, not what
    /// it does for them. Matched as whole words, ignoring case.
    /// </summary>
    public static readonly IReadOnlyList<string> Jargon =
    [
        "prompt", "prompts", "model", "models", "llm", "token", "tokens", "embedding", "embeddings", "vector",
        "vectors", "blob", "blobs", "sql", "database", "api", "json", "endpoint", "endpoints", "schema", "payload",
        "guid", "uuid", "provider", "providers", "state machine", "status machine", "workflow engine",
    ];

    public static IReadOnlyList<WorkflowDefinitionError> Validate(WorkflowDefinition definition) =>
        Validate(definition, WorkflowCapabilities.ByKey);

    public static IReadOnlyList<WorkflowDefinitionError> Validate(
        WorkflowDefinition definition,
        IReadOnlyDictionary<WorkflowCapabilityKey, WorkflowCapability> capabilities)
    {
        var errors = new List<WorkflowDefinitionError>();
        var root = $"{definition.Key}@{definition.Version}";

        if (!IsDottedKey(definition.Key.Value))
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.KeyInvalid, root,
                $"'{definition.Key}' is not a lowercase dotted key such as 'recipe.create'."));
        }

        if (definition.Version < 1)
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.VersionInvalid, root, "A version starts at 1."));
        }

        CheckCopy(errors, $"{root}/goal", definition.Goal, MaxGoalLength);
        CheckCopy(errors, $"{root}/description", definition.Description, MaxDescriptionLength);
        CheckCopy(errors, $"{root}/expectedOutcome", definition.ExpectedOutcome, MaxOutcomeLength);

        if (definition.UnavailableReason is not null)
        {
            CheckCopy(errors, $"{root}/unavailableReason", definition.UnavailableReason, MaxReasonLength);

            // Honest both ways: a journey that says it cannot be taken must really rest on something unbuilt.
            var blocked = definition.Steps.Any(step => step.Requirement == WorkflowStepRequirement.Required
                && capabilities.TryGetValue(step.Capability.Primary, out var capability)
                && !capability.IsAvailable);
            if (!blocked)
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.UnavailableNotBlocked, $"{root}/unavailableReason",
                    "Every required step can be done, so the workflow is available and must not say otherwise."));
            }
        }

        // What the workflow guarantees before any step runs. A step cannot be complete before the run starts.
        var startingKinds = new HashSet<WorkflowResourceKind>();
        for (var i = 0; i < definition.Prerequisites.Count; i++)
        {
            var path = $"{root}/prerequisites[{i}]";
            switch (definition.Prerequisites[i])
            {
                case WorkflowPrerequisite.StepCompleted:
                    errors.Add(new(WorkflowDefinitionErrorCodes.PrerequisiteNotAllowed, path,
                        "A workflow cannot require one of its own steps before it starts."));
                    break;
                case WorkflowPrerequisite.ResourceLinked linked when CheckKind(errors, path, linked.Kind):
                    startingKinds.Add(linked.Kind);
                    break;
                case WorkflowPrerequisite.WorkspaceFact fact:
                    CheckFact(errors, path, fact.Fact);
                    break;
            }
        }

        if (definition.Steps.Count == 0)
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.StepsEmpty, root, "A workflow has at least one step."));
        }
        else if (!definition.Steps.Any(step => step.Requirement == WorkflowStepRequirement.Required))
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.StepsNoneRequired, root,
                "A workflow has at least one required step, or completing it would prove nothing."));
        }

        var earlierSteps = new HashSet<WorkflowStepKey>();
        // Kinds a run is certain to hold by the time it reaches a step: the starting prerequisites, and whatever
        // an earlier *required* step linked. An optional step may have been skipped, so it guarantees nothing.
        var linkedKinds = new HashSet<WorkflowResourceKind>(startingKinds);

        foreach (var step in definition.Steps)
        {
            var path = $"{root}/steps[{step.Key}]";

            if (!IsSegment(step.Key.Value))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.StepKeyInvalid, path,
                    $"'{step.Key}' is not a lowercase step key such as 'ingredients'."));
            }

            if (earlierSteps.Contains(step.Key))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.StepKeyDuplicate, path,
                    $"Another step is already keyed '{step.Key}'."));
            }

            if (!Enum.IsDefined(step.Requirement))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.StepRequirementInvalid, path,
                    $"'{step.Requirement}' is neither Required nor Optional."));
            }

            CheckCopy(errors, $"{path}/title", step.Title, MaxTitleLength);
            CheckCopy(errors, $"{path}/help", step.Help, MaxHelpLength);

            var available = new HashSet<WorkflowResourceKind>(linkedKinds);
            for (var i = 0; i < step.Prerequisites.Count; i++)
            {
                var prerequisitePath = $"{path}/prerequisites[{i}]";
                switch (step.Prerequisites[i])
                {
                    case WorkflowPrerequisite.StepCompleted completed when !earlierSteps.Contains(completed.Step):
                        errors.Add(new(WorkflowDefinitionErrorCodes.PrerequisiteStepNotEarlier, prerequisitePath,
                            $"'{completed.Step}' is not an earlier step, so waiting for it could never end."));
                        break;
                    // A step that requires the link is guarded by that requirement at runtime, so it may read it.
                    case WorkflowPrerequisite.ResourceLinked linked when CheckKind(errors, prerequisitePath, linked.Kind):
                        available.Add(linked.Kind);
                        break;
                    case WorkflowPrerequisite.WorkspaceFact fact:
                        CheckFact(errors, prerequisitePath, fact.Fact);
                        break;
                }
            }

            CheckCapabilityLink(errors, $"{path}/capability", step, capabilities, available, definition.IsAvailable);
            CheckCompletionRule(errors, $"{path}/completionRule", step, available);

            earlierSteps.Add(step.Key);
            if (step.Requirement == WorkflowStepRequirement.Required
                && step.CompletionRule is WorkflowCompletionRule.ResourceLinked produced)
            {
                linkedKinds.UnionWith(produced.Kinds);
            }
        }

        CheckSuggestions(errors, root, definition, capabilities);
        CheckStartsFrom(errors, root, definition);

        return errors;
    }

    /// <summary>
    /// Each kind a run may start from is one the journey reads — a step links it or waits for it — so a starting
    /// record is never a link nothing uses.
    /// </summary>
    private static void CheckStartsFrom(List<WorkflowDefinitionError> errors, string root, WorkflowDefinition definition)
    {
        var read = definition.Prerequisites.OfType<WorkflowPrerequisite.ResourceLinked>().Select(linked => linked.Kind)
            .Concat(definition.Steps.SelectMany(step => step.Prerequisites.OfType<WorkflowPrerequisite.ResourceLinked>()
                .Select(linked => linked.Kind)))
            .Concat(definition.Steps.Select(step => step.CompletionRule).OfType<WorkflowCompletionRule.ResourceLinked>()
                .SelectMany(linked => linked.Kinds))
            .ToHashSet();

        for (var i = 0; i < definition.StartsFrom.Count; i++)
        {
            var kind = definition.StartsFrom[i];
            var path = $"{root}/startsFrom[{i}]";

            if (!CheckKind(errors, path, kind))
            {
                continue;
            }

            if (definition.StartsFrom.Take(i).Contains(kind))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.StartsFromDuplicate, path, $"'{kind}' is named more than once."));
            }
            else if (!read.Contains(kind))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.StartsFromUnread, path,
                    $"No step links or waits for a {kind}, so starting from one would link something nothing uses."));
            }
        }
    }

    /// <summary>Checks the capability registry itself: keys, names, routes, and that an unavailable one says why.</summary>
    public static IReadOnlyList<WorkflowDefinitionError> ValidateCapabilities(IEnumerable<WorkflowCapability> capabilities)
    {
        var errors = new List<WorkflowDefinitionError>();
        var seen = new HashSet<WorkflowCapabilityKey>();

        foreach (var capability in capabilities)
        {
            var path = $"capabilities[{capability.Key}]";

            if (!IsDottedKey(capability.Key.Value))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.KeyInvalid, path,
                    $"'{capability.Key}' is not a lowercase dotted key such as 'recipes.edit'."));
            }

            if (!seen.Add(capability.Key))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.CapabilityDuplicate, path,
                    $"'{capability.Key}' is registered twice."));
            }

            CheckCopy(errors, $"{path}/name", capability.Name, MaxCapabilityNameLength);

            if (capability.UnavailableReason is not null)
            {
                CheckCopy(errors, $"{path}/unavailableReason", capability.UnavailableReason, MaxReasonLength);
            }

            if (capability.DraftKind is { } draftKind)
            {
                CheckKind(errors, $"{path}/draftKind", draftKind);
            }

            if (RoutePlaceholderKinds(capability.RouteTemplate) is null)
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.CapabilityRouteInvalid, $"{path}/routeTemplate",
                    $"'{capability.RouteTemplate}' is not a relative route of lowercase segments and known placeholders."));
            }
        }

        return errors;
    }

    /// <summary>
    /// The resource kinds a route template's placeholders need, or null when the template is malformed or names
    /// a placeholder that is not in <see cref="WorkflowResourceKinds.RoutePlaceholders"/>.
    /// </summary>
    /// <remarks>
    /// A template is lowercase segments and placeholders, optionally ending in one fixed <c>?name=value</c> — the
    /// form a page uses to open one of its own areas (<c>recipes/{recipeId}?area=tests</c>). The value is literal:
    /// nothing from a run is ever written into a query.
    /// </remarks>
    public static IReadOnlySet<WorkflowResourceKind>? RoutePlaceholderKinds(string routeTemplate)
    {
        if (string.IsNullOrEmpty(routeTemplate))
        {
            return null;
        }

        var parts = routeTemplate.Split('?');
        if (parts.Length > 2)
        {
            return null;
        }

        if (parts.Length == 2)
        {
            var query = parts[1].Split('=');
            if (query.Length != 2 || !IsSegment(query[0]) || !IsSegment(query[1]))
            {
                return null;
            }
        }

        var kinds = new HashSet<WorkflowResourceKind>();
        foreach (var segment in parts[0].Split('/'))
        {
            if (segment.Length > 2 && segment[0] == '{' && segment[^1] == '}')
            {
                if (!WorkflowResourceKinds.RoutePlaceholders.TryGetValue(segment[1..^1], out var kind))
                {
                    return null;
                }

                kinds.Add(kind);
            }
            else if (!IsSegment(segment))
            {
                return null;
            }
        }

        return kinds;
    }

    internal static bool IsDottedKey(string? key) =>
        key is { Length: > 0 and <= MaxKeyLength } && DottedKeyPattern().IsMatch(key);

    private static bool IsSegment(string? key) =>
        key is { Length: > 0 and <= MaxKeyLength } && SegmentPattern().IsMatch(key);

    private static void CheckCapabilityLink(
        List<WorkflowDefinitionError> errors,
        string path,
        WorkflowStepDefinition step,
        IReadOnlyDictionary<WorkflowCapabilityKey, WorkflowCapability> capabilities,
        HashSet<WorkflowResourceKind> available,
        bool definitionAvailable)
    {
        var link = step.Capability;

        if (link.Alternatives.Count > MaxAlternatives)
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.CapabilityTooManyAlternatives, path,
                $"A step offers one primary action and at most {MaxAlternatives} alternatives."));
        }

        var named = new HashSet<WorkflowCapabilityKey>();
        var all = new[] { link.Primary }.Concat(link.Alternatives).ToList();
        for (var i = 0; i < all.Count; i++)
        {
            var key = all[i];
            var keyPath = i == 0 ? $"{path}/primary" : $"{path}/alternatives[{i - 1}]";

            if (!named.Add(key))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.CapabilityDuplicate, keyPath,
                    $"'{key}' is offered twice on one step."));
                continue;
            }

            if (!capabilities.TryGetValue(key, out var capability))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.CapabilityUnknown, keyPath,
                    $"'{key}' is not a registered capability."));
                continue;
            }

            // Unless the definition itself says it cannot be taken yet, and why (WorkflowDefinition.UnavailableReason).
            if (i == 0 && definitionAvailable && !capability.IsAvailable && step.Requirement == WorkflowStepRequirement.Required)
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.CapabilityUnavailableForRequiredStep, keyPath,
                    $"'{key}' is not available yet, and a required step on it would make the workflow impossible to finish."));
            }

            // A malformed route is the registry's defect, reported by ValidateCapabilities, not once per step.
            var needs = RoutePlaceholderKinds(capability.RouteTemplate);
            foreach (var kind in needs ?? Enumerable.Empty<WorkflowResourceKind>())
            {
                if (!available.Contains(kind))
                {
                    errors.Add(new(WorkflowDefinitionErrorCodes.ResourceNotYetLinked, keyPath,
                        $"'{key}' opens a {kind} the run is not certain to hold by this step."));
                }
            }
        }
    }

    private static void CheckCompletionRule(
        List<WorkflowDefinitionError> errors,
        string path,
        WorkflowStepDefinition step,
        HashSet<WorkflowResourceKind> available)
    {
        switch (step.CompletionRule)
        {
            case WorkflowCompletionRule.ResourceLinked linked:
                if (linked.Kinds.Count == 0)
                {
                    errors.Add(new(WorkflowDefinitionErrorCodes.CompletionKindsEmpty, path,
                        "Name the kind of record the step links, or nothing could ever show it is done."));
                }

                foreach (var kind in linked.Kinds)
                {
                    CheckKind(errors, path, kind);
                }

                foreach (var duplicate in linked.Kinds.GroupBy(kind => kind).Where(group => group.Count() > 1))
                {
                    errors.Add(new(WorkflowDefinitionErrorCodes.CompletionKindDuplicate, path,
                        $"'{duplicate.Key}' is named more than once."));
                }

                break;

            case WorkflowCompletionRule.RecipeSavedDuringRun:
                RequireKind(errors, path, available, WorkflowResourceKind.Recipe);
                break;

            case WorkflowCompletionRule.LongFormDraftStatusIn draft:
                RequireKind(errors, path, available, WorkflowResourceKind.LongFormDraft);
                CheckStatuses(errors, path, draft.Statuses);
                break;

            case WorkflowCompletionRule.BrandGuideIsActive:
                RequireKind(errors, path, available, WorkflowResourceKind.BrandStyleGuide);
                break;

            case WorkflowCompletionRule.SocialPostAccepted:
                RequireKind(errors, path, available, WorkflowResourceKind.CreativeContext);
                break;

            case WorkflowCompletionRule.PublishingRecordStatusIn record:
                RequireKind(errors, path, available, WorkflowResourceKind.PublishingRecord);
                CheckStatuses(errors, path, record.Statuses);
                break;

            case WorkflowCompletionRule.ReadinessRulesMet readiness:
                RequireRecipe(errors, path, available);
                if (readiness.RuleIds.Count == 0)
                {
                    errors.Add(new(WorkflowDefinitionErrorCodes.CompletionReadinessRulesEmpty, path,
                        "Name at least one readiness rule, or the step is done before it starts."));
                }

                foreach (var ruleId in readiness.RuleIds.Where(id => !RecipeReadinessCatalogue.Rules.Any(rule => rule.Id == id)))
                {
                    errors.Add(new(WorkflowDefinitionErrorCodes.CompletionReadinessRuleUnknown, path,
                        $"'{ruleId}' is not a rule in the recipe readiness catalogue."));
                }

                break;

            case WorkflowCompletionRule.RecipeStatusIn statuses:
                RequireRecipe(errors, path, available);
                CheckStatuses(errors, path, statuses.Statuses);
                break;

            case WorkflowCompletionRule.Acknowledged when step.Requirement == WorkflowStepRequirement.Required:
                errors.Add(new(WorkflowDefinitionErrorCodes.CompletionAcknowledgedOnRequiredStep, path,
                    "A required step is proved by a fact the server checks, never by the creator's word alone."));
                break;
        }
    }

    private static void CheckSuggestions(
        List<WorkflowDefinitionError> errors,
        string root,
        WorkflowDefinition definition,
        IReadOnlyDictionary<WorkflowCapabilityKey, WorkflowCapability> capabilities)
    {
        if (definition.NextSuggestions.Count > MaxSuggestions)
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.SuggestionsTooMany, $"{root}/nextSuggestions",
                $"At most {MaxSuggestions} suggestions, so one of them can be the obvious next thing."));
        }

        var seen = new HashSet<WorkflowSuggestion>(new SuggestionTargetComparer());
        for (var i = 0; i < definition.NextSuggestions.Count; i++)
        {
            var suggestion = definition.NextSuggestions[i];
            var path = $"{root}/nextSuggestions[{i}]";

            CheckCopy(errors, $"{path}/why", suggestion.Why, MaxReasonLength);

            if (!seen.Add(suggestion))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.SuggestionDuplicate, path, "This is already suggested."));
            }

            switch (suggestion)
            {
                case WorkflowSuggestion.Workflow workflow when workflow.Key == definition.Key:
                    errors.Add(new(WorkflowDefinitionErrorCodes.SuggestionSelf, path,
                        "A workflow does not suggest itself; Start over is its own action."));
                    break;
                case WorkflowSuggestion.Workflow workflow when !IsDottedKey(workflow.Key.Value):
                    errors.Add(new(WorkflowDefinitionErrorCodes.KeyInvalid, path,
                        $"'{workflow.Key}' is not a lowercase dotted key."));
                    break;
                case WorkflowSuggestion.Capability capability when !capabilities.ContainsKey(capability.Key):
                    errors.Add(new(WorkflowDefinitionErrorCodes.CapabilityUnknown, path,
                        $"'{capability.Key}' is not a registered capability."));
                    break;
            }
        }
    }

    private static void RequireRecipe(List<WorkflowDefinitionError> errors, string path, HashSet<WorkflowResourceKind> available) =>
        RequireKind(errors, path, available, WorkflowResourceKind.Recipe);

    private static void RequireKind(
        List<WorkflowDefinitionError> errors,
        string path,
        HashSet<WorkflowResourceKind> available,
        WorkflowResourceKind kind)
    {
        if (!available.Contains(kind))
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.ResourceNotYetLinked, path,
                $"This reads the run's {kind}, which the run is not certain to hold by this step."));
        }
    }

    private static void CheckStatuses<TStatus>(List<WorkflowDefinitionError> errors, string path, IReadOnlyList<TStatus> statuses)
        where TStatus : struct, Enum
    {
        if (statuses.Count == 0)
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.CompletionStatusesEmpty, path,
                "Name at least one state, or the step can never be done."));
        }

        foreach (var status in statuses.Where(status => !Enum.IsDefined(status)))
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.CompletionStatusUnknown, path,
                $"'{status}' is not a {typeof(TStatus).Name}."));
        }
    }

    private static void CheckFact(List<WorkflowDefinitionError> errors, string path, WorkflowWorkspaceFact fact)
    {
        if (!Enum.IsDefined(fact))
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.PrerequisiteFactUnknown, path, $"'{fact}' is not a workspace fact."));
        }
    }

    private static bool CheckKind(List<WorkflowDefinitionError> errors, string path, WorkflowResourceKind kind)
    {
        if (Enum.IsDefined(kind))
        {
            return true;
        }

        errors.Add(new(WorkflowDefinitionErrorCodes.ResourceKindUnknown, path, $"'{kind}' is not a resource kind."));
        return false;
    }

    private static void CheckCopy(List<WorkflowDefinitionError> errors, string path, string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.CopyMissing, path, "Say this in plain words; it may not be blank."));
            return;
        }

        if (text.Length > maxLength)
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.CopyTooLong, path,
                $"{text.Length} characters, over the {maxLength} this copy may use."));
        }

        foreach (var word in Jargon.Where(word =>
                     Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        {
            errors.Add(new(WorkflowDefinitionErrorCodes.CopyJargon, path,
                $"'{word}' is how the product works inside, not something a creator should need to know."));
        }
    }

    /// <summary>Two suggestions are the same when they open the same thing, whatever reason each gives.</summary>
    private sealed class SuggestionTargetComparer : IEqualityComparer<WorkflowSuggestion>
    {
        public bool Equals(WorkflowSuggestion? x, WorkflowSuggestion? y) => Target(x) == Target(y);

        public int GetHashCode(WorkflowSuggestion obj) => Target(obj).GetHashCode(StringComparison.Ordinal);

        private static string Target(WorkflowSuggestion? suggestion) => suggestion switch
        {
            WorkflowSuggestion.Workflow workflow => $"workflow:{workflow.Key}",
            WorkflowSuggestion.Capability capability => $"capability:{capability.Key}",
            _ => string.Empty,
        };
    }

    // area.goal at least: two or more dot-separated segments, each lowercase words joined by single hyphens.
    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*(\\.[a-z][a-z0-9]*(-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedKeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
}
