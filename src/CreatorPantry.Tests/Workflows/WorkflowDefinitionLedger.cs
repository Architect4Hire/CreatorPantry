using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// What a shipped workflow version looked like, and whether it still does. The version half of 13A.1.
/// </summary>
/// <remarks>
/// <para>
/// A run pins the definition version it started on (13A.6), so a shipped version is frozen: editing one would
/// change a journey under someone part-way through it, and removing one would leave their run unresumable.
/// Any change — copy included — is a new version instead (13A.1, approved).
/// </para>
/// <para>
/// The record is a canonical JSON form per <c>key@version</c>, kept in the reviewed snapshot
/// <c>Snapshots/workflow-definitions.json</c>. It is stricter than the OpenAPI snapshot by design: regenerating it
/// only ever <em>adds</em> entries, and <see cref="Append"/> refuses outright when a shipped entry changed or
/// vanished, so no flag re-baselines a frozen version.
/// </para>
/// </remarks>
internal static class WorkflowDefinitionLedger
{
    /// <summary>Relaxed so the snapshot reads as the copy does: an apostrophe stays an apostrophe.</summary>
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonWriterOptions Indented = Compact with { Indented = true, NewLine = "\n" };

    public static string EntryKey(WorkflowDefinition definition) => $"{definition.Key}@{definition.Version}";

    public static IReadOnlyDictionary<string, string> Entries(IEnumerable<WorkflowDefinition> definitions) =>
        definitions.ToDictionary(EntryKey, Canonical, StringComparer.Ordinal);

    /// <summary>Every shipped entry that now differs from, or is missing in, the current one.</summary>
    public static IReadOnlyList<string> Violations(
        IReadOnlyDictionary<string, string> shipped,
        IReadOnlyDictionary<string, string> current)
    {
        var violations = new List<string>();

        foreach (var (key, recorded) in shipped.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!current.TryGetValue(key, out var now))
            {
                violations.Add($"{key} was shipped and has been removed; a run may still be pinned to it.");
            }
            else if (!string.Equals(recorded, now, StringComparison.Ordinal))
            {
                violations.Add($"{key} was shipped and has changed; make the change a new version instead.");
            }
        }

        return violations;
    }

    /// <summary>The shipped record plus every new entry. Refuses when any shipped entry changed or vanished.</summary>
    public static IReadOnlyDictionary<string, string> Append(
        IReadOnlyDictionary<string, string> shipped,
        IReadOnlyDictionary<string, string> current)
    {
        var violations = Violations(shipped, current);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, violations));
        }

        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in shipped.Concat(current))
        {
            merged[key] = value;
        }

        return merged;
    }

    /// <summary>Reads a snapshot file back into canonical entries.</summary>
    public static IReadOnlyDictionary<string, string> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            entries[property.Name] = Write(property.Value.WriteTo, Compact);
        }

        return entries;
    }

    /// <summary>Writes canonical entries as the reviewed, indented snapshot file.</summary>
    public static string Render(IReadOnlyDictionary<string, string> entries) =>
        Write(json =>
        {
            json.WriteStartObject();
            foreach (var (key, canonical) in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                using var entry = JsonDocument.Parse(canonical);
                json.WritePropertyName(key);
                entry.RootElement.WriteTo(json);
            }

            json.WriteEndObject();
        }, Indented) + "\n";

    /// <summary>
    /// The definition as ordered JSON. Every field is written, in declaration order, with the closed rule types
    /// named explicitly, so two definitions write the same text exactly when they are the same journey.
    /// </summary>
    public static string Canonical(WorkflowDefinition definition) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("key", definition.Key.Value);
        json.WriteNumber("version", definition.Version);
        json.WriteString("goal", definition.Goal);
        json.WriteString("description", definition.Description);
        json.WriteString("expectedOutcome", definition.ExpectedOutcome);
        if (definition.UnavailableReason is not null)
        {
            json.WriteString("unavailableReason", definition.UnavailableReason);
        }

        WritePrerequisites(json, definition.Prerequisites);
        WriteStrings(json, "startsFrom", definition.StartsFrom.Select(kind => kind.ToString()));

        json.WriteStartArray("steps");
        foreach (var step in definition.Steps)
        {
            json.WriteStartObject();
            json.WriteString("key", step.Key.Value);
            json.WriteString("title", step.Title);
            json.WriteString("help", step.Help);
            json.WriteString("requirement", step.Requirement.ToString());
            json.WriteStartObject("capability");
            json.WriteString("primary", step.Capability.Primary.Value);
            WriteStrings(json, "alternatives", step.Capability.Alternatives.Select(alternative => alternative.Value));
            json.WriteEndObject();
            WritePrerequisites(json, step.Prerequisites);
            WriteCompletionRule(json, step.CompletionRule);
            json.WriteEndObject();
        }

        json.WriteEndArray();

        json.WriteStartArray("nextSuggestions");
        foreach (var suggestion in definition.NextSuggestions)
        {
            json.WriteStartObject();
            switch (suggestion)
            {
                case WorkflowSuggestion.Workflow workflow:
                    json.WriteString("workflow", workflow.Key.Value);
                    break;
                case WorkflowSuggestion.Capability capability:
                    json.WriteString("capability", capability.Key.Value);
                    break;
                default:
                    throw new NotSupportedException($"No canonical form for {suggestion.GetType().Name}.");
            }

            json.WriteString("why", suggestion.Why);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }, Compact);

    private static string Write(Action<Utf8JsonWriter> write, JsonWriterOptions options)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, options))
        {
            write(json);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WritePrerequisites(Utf8JsonWriter json, IReadOnlyList<WorkflowPrerequisite> prerequisites)
    {
        json.WriteStartArray("prerequisites");
        foreach (var prerequisite in prerequisites)
        {
            json.WriteStartObject();
            switch (prerequisite)
            {
                case WorkflowPrerequisite.StepCompleted completed:
                    json.WriteString("stepCompleted", completed.Step.Value);
                    break;
                case WorkflowPrerequisite.ResourceLinked linked:
                    json.WriteString("resourceLinked", linked.Kind.ToString());
                    break;
                case WorkflowPrerequisite.WorkspaceFact fact:
                    json.WriteString("workspaceFact", fact.Fact.ToString());
                    break;
                default:
                    throw new NotSupportedException($"No canonical form for {prerequisite.GetType().Name}.");
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteCompletionRule(Utf8JsonWriter json, WorkflowCompletionRule rule)
    {
        json.WriteStartObject("completionRule");
        switch (rule)
        {
            case WorkflowCompletionRule.ResourceLinked linked:
                WriteStrings(json, "resourceLinked", linked.Kinds.Select(kind => kind.ToString()));
                break;
            case WorkflowCompletionRule.ReadinessRulesMet readiness:
                WriteStrings(json, "readinessRulesMet", readiness.RuleIds);
                break;
            case WorkflowCompletionRule.RecipeStatusIn statuses:
                WriteStrings(json, "recipeStatusIn", statuses.Statuses.Select(status => status.ToString()));
                break;
            case WorkflowCompletionRule.RecipeSavedDuringRun:
                json.WriteBoolean("recipeSavedDuringRun", true);
                break;
            case WorkflowCompletionRule.LongFormDraftStatusIn statuses:
                WriteStrings(json, "longFormDraftStatusIn", statuses.Statuses.Select(status => status.ToString()));
                break;
            case WorkflowCompletionRule.BrandGuideIsActive:
                json.WriteBoolean("brandGuideIsActive", true);
                break;
            case WorkflowCompletionRule.SocialPostAccepted:
                json.WriteBoolean("socialPostAccepted", true);
                break;
            case WorkflowCompletionRule.PublishingRecordStatusIn statuses:
                WriteStrings(json, "publishingRecordStatusIn", statuses.Statuses.Select(status => status.ToString()));
                break;
            case WorkflowCompletionRule.Acknowledged:
                json.WriteBoolean("acknowledged", true);
                break;
            default:
                throw new NotSupportedException($"No canonical form for {rule.GetType().Name}.");
        }

        json.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }
}
