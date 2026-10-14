namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>
/// Checks a set of definitions together: what no single definition can know about itself.
/// </summary>
/// <remarks>
/// Every definition is validated on its own first, then: one entry per key and version; each key's versions run
/// 1, 2, 3… with none missing, because a run may be pinned to any of them; and every suggested workflow is one
/// the set defines. Whether a shipped version has since been edited is a question about history, not about
/// the set, and is answered against the reviewed snapshot instead.
/// </remarks>
public static class WorkflowCatalogueValidator
{
    public static IReadOnlyList<WorkflowDefinitionError> Validate(IReadOnlyCollection<WorkflowDefinition> definitions) =>
        Validate(definitions, WorkflowCapabilities.All);

    public static IReadOnlyList<WorkflowDefinitionError> Validate(
        IReadOnlyCollection<WorkflowDefinition> definitions,
        IReadOnlyCollection<WorkflowCapability> capabilities)
    {
        var errors = new List<WorkflowDefinitionError>(WorkflowDefinitionValidator.ValidateCapabilities(capabilities));

        var byKey = capabilities
            .GroupBy(capability => capability.Key)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var definition in definitions)
        {
            errors.AddRange(WorkflowDefinitionValidator.Validate(definition, byKey));
        }

        foreach (var family in definitions.GroupBy(definition => definition.Key))
        {
            var versions = family.Select(definition => definition.Version).ToList();

            foreach (var duplicate in versions.GroupBy(version => version).Where(group => group.Count() > 1))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.CatalogueDuplicateVersion, $"{family.Key}@{duplicate.Key}",
                    $"{duplicate.Count()} definitions claim this key and version."));
            }

            var distinct = versions.Distinct().Order().ToList();
            var expected = Enumerable.Range(1, distinct.Count);
            if (distinct.Count > 0 && !distinct.SequenceEqual(expected))
            {
                errors.Add(new(WorkflowDefinitionErrorCodes.CatalogueVersionGap, family.Key.Value,
                    $"Versions are {string.Join(", ", distinct)}; they must run 1 to {distinct.Count} with none missing."));
            }
        }

        var known = definitions.Select(definition => definition.Key).ToHashSet();
        foreach (var definition in definitions)
        {
            for (var i = 0; i < definition.NextSuggestions.Count; i++)
            {
                if (definition.NextSuggestions[i] is WorkflowSuggestion.Workflow workflow
                    && WorkflowDefinitionValidator.IsDottedKey(workflow.Key.Value)
                    && workflow.Key != definition.Key
                    && !known.Contains(workflow.Key))
                {
                    errors.Add(new(WorkflowDefinitionErrorCodes.SuggestionUnknownWorkflow,
                        $"{definition.Key}@{definition.Version}/nextSuggestions[{i}]",
                        $"'{workflow.Key}' is not a workflow in this catalogue."));
                }
            }
        }

        return errors;
    }
}
