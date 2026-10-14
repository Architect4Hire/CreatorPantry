using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// The shipped workflow catalogue (13A.4): that it is valid, that it is the approved one, that every step points at
/// something built or says it is not, and that no shipped version changes in place.
/// </summary>
public sealed partial class WorkflowCatalogueTests
{
    [Fact]
    public void The_catalogue_is_valid()
    {
        var errors = WorkflowCatalogueValidator.Validate(WorkflowCatalogue.All);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void The_catalogue_is_the_approved_ten_at_version_one_with_their_step_counts()
    {
        Assert.Equal(
            [
                ("recipe.create", 1, 5),
                ("recipe.improve", 1, 5),
                ("recipe.test-and-approve", 1, 6),
                ("brand.voice", 1, 5),
                ("content.blog-and-seo", 1, 6),
                ("images.create", 1, 4),
                ("social.create", 1, 4),
                ("assets.organize", 1, 4),
                ("publishing.plan-and-publish", 1, 4),
                ("planning.day-and-week", 1, 3),
            ],
            WorkflowCatalogue.All.Select(definition => (definition.Key.Value, definition.Version, definition.Steps.Count)));
    }

    [Fact]
    public void Only_the_planner_is_unavailable_and_it_says_why()
    {
        var unavailable = Assert.Single(WorkflowCatalogue.All, definition => !definition.IsAvailable);

        Assert.Equal(WorkflowCatalogue.PlanDayAndWeekKey, unavailable.Key.Value);
        Assert.False(string.IsNullOrWhiteSpace(unavailable.UnavailableReason));
    }

    [Fact]
    public void Every_required_step_of_an_available_workflow_can_be_done_today()
    {
        var offenders = WorkflowCatalogue.All
            .Where(definition => definition.IsAvailable)
            .SelectMany(definition => definition.Steps
                .Where(step => step.Requirement == WorkflowStepRequirement.Required
                    && !WorkflowCapabilities.ByKey[step.Capability.Primary].IsAvailable)
                .Select(step => $"{definition.Key}/{step.Key}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_workflow_suggests_one_that_cannot_be_started()
    {
        var unavailable = WorkflowCatalogue.All.Where(definition => !definition.IsAvailable).Select(definition => definition.Key).ToHashSet();

        var offenders = WorkflowCatalogue.All
            .SelectMany(definition => definition.NextSuggestions
                .OfType<WorkflowSuggestion.Workflow>()
                .Where(suggestion => unavailable.Contains(suggestion.Key))
                .Select(suggestion => $"{definition.Key} -> {suggestion.Key}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_registered_capability_is_used_by_the_catalogue()
    {
        // A registered capability nothing names is a route nobody checks a journey against — and usually a typo
        // on one side or the other.
        var used = WorkflowCatalogue.All
            .SelectMany(definition => definition.Steps
                .SelectMany(step => new[] { step.Capability.Primary }.Concat(step.Capability.Alternatives))
                .Concat(definition.NextSuggestions.OfType<WorkflowSuggestion.Capability>().Select(suggestion => suggestion.Key)))
            .ToHashSet();

        Assert.Empty(WorkflowCapabilities.All.Select(capability => capability.Key).Where(key => !used.Contains(key)));
    }

    [Fact]
    public void Every_kind_a_workflow_starts_from_can_be_checked_when_a_run_starts()
    {
        // A kind with no check would reach the start and fail as a server error, not a refusal.
        var uncheckable = WorkflowCatalogue.All
            .SelectMany(definition => definition.StartsFrom.Select(kind => (definition.Key, kind)))
            .Where(entry => !Domain.Modules.Workflows.Business.WorkflowRunBusiness.CheckableKinds.Contains(entry.kind))
            .Select(entry => $"{entry.Key}: {entry.kind}")
            .ToList();

        Assert.Empty(uncheckable);
    }

    [Fact]
    public void Every_kind_a_capability_keeps_a_draft_in_can_be_checked_when_one_is_saved()
    {
        var uncheckable = WorkflowCapabilities.All
            .Where(capability => capability.DraftKind is { } kind
                && !Domain.Modules.Workflows.Business.WorkflowRunBusiness.CheckableKinds.Contains(kind))
            .Select(capability => $"{capability.Key}: {capability.DraftKind}")
            .ToList();

        Assert.Empty(uncheckable);
    }

    [Fact]
    public void Find_resolves_a_pinned_version_and_nothing_else()
    {
        Assert.Same(WorkflowCatalogue.RecipeCreateV1, WorkflowCatalogue.Find(WorkflowCatalogue.RecipeCreateKey, 1));
        Assert.Null(WorkflowCatalogue.Find(WorkflowCatalogue.RecipeCreateKey, 2));
        Assert.Null(WorkflowCatalogue.Find("recipe.nowhere", 1));
        Assert.Equal(WorkflowCatalogue.All.Count, WorkflowCatalogue.Latest().Count);
    }

    /// <summary>
    /// Every capability route names addresses the web app has. A typo guard over the app's own sources rather than
    /// a router: the first segment must be a top-level path in <c>app.routes.ts</c>, and every other literal —
    /// a nested path, a long-form step, an area the recipe page opens — must appear quoted in the route, step and
    /// page sources that define them.
    /// </summary>
    [Fact]
    public void Every_capability_route_names_addresses_the_web_app_has()
    {
        var app = WebAppRoot();
        var topLevel = PathLiteral().Matches(File.ReadAllText(Path.Combine(app, "app.routes.ts")))
            .Select(match => match.Groups[1].Value.Split('/')[0])
            .ToHashSet(StringComparer.Ordinal);

        var sources = Directory.EnumerateFiles(Path.Combine(app, "features"), "*.routes.ts", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(app, "models"), "*.models.ts"))
            .Append(Path.Combine(app, "app.routes.ts"))
            .Append(Path.Combine(app, "features", "recipes", "recipe-editor.component.ts"))
            .Append(Path.Combine(app, "shared", "use-this-in", "handoff-destinations.ts"));
        var quoted = sources
            .SelectMany(file => QuotedLiteral().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .SelectMany(literal => literal.Split('/'))
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        foreach (var capability in WorkflowCapabilities.All)
        {
            var parts = capability.RouteTemplate.Split('?');
            var segments = parts[0].Split('/');

            if (!topLevel.Contains(segments[0]))
            {
                offenders.Add($"{capability.Key}: '{segments[0]}' is not a top-level route");
            }

            var literals = segments.Skip(1).Where(segment => !segment.StartsWith('{'))
                .Concat(parts.Length == 2 ? new[] { parts[1].Split('=')[1] } : Array.Empty<string>());
            offenders.AddRange(literals.Where(literal => !quoted.Contains(literal))
                .Select(literal => $"{capability.Key}: '{literal}' appears nowhere in the web app's routes"));
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// No shipped version changes or disappears. Regenerate with <c>UPDATE_WORKFLOW_SNAPSHOT=1</c> to record a new
    /// version; that can only add entries, and refuses when a recorded one has changed.
    /// </summary>
    [Fact]
    public void No_shipped_version_changes_in_place()
    {
        var path = SnapshotPath();
        var current = WorkflowDefinitionLedger.Entries(WorkflowCatalogue.All);
        var shipped = File.Exists(path)
            ? WorkflowDefinitionLedger.Read(File.ReadAllText(path))
            : new Dictionary<string, string>();

        if (Environment.GetEnvironmentVariable("UPDATE_WORKFLOW_SNAPSHOT") == "1")
        {
            File.WriteAllText(path, WorkflowDefinitionLedger.Render(WorkflowDefinitionLedger.Append(shipped, current)));
            return;
        }

        Assert.True(File.Exists(path), $"No snapshot at {path}; run with UPDATE_WORKFLOW_SNAPSHOT=1 and review it.");

        var violations = WorkflowDefinitionLedger.Violations(shipped, current);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));

        var unrecorded = current.Keys.Except(shipped.Keys).ToList();
        Assert.True(
            unrecorded.Count == 0,
            $"Not yet recorded: {string.Join(", ", unrecorded)}. Run with UPDATE_WORKFLOW_SNAPSHOT=1 and review the diff.");
    }

    [Fact]
    public void The_snapshot_round_trips_exactly()
    {
        var entries = WorkflowDefinitionLedger.Entries(WorkflowCatalogue.All);

        var read = WorkflowDefinitionLedger.Read(WorkflowDefinitionLedger.Render(entries));

        Assert.Equal(entries.OrderBy(entry => entry.Key), read.OrderBy(entry => entry.Key));
    }

    private static string SnapshotPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots", "workflow-definitions.json");

    private static string WebAppRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "web", "src", "app"));

    [GeneratedRegex(@"path:\s*'([^']*)'")]
    private static partial Regex PathLiteral();

    // Takes route paths with parameters in them (':guideId/test-drive') as well as plain literals; a ':param'
    // segment it yields matches no literal of a template, so splitting it out costs nothing.
    [GeneratedRegex(@"'(:?[A-Za-z0-9][A-Za-z0-9\-/:]*)'")]
    private static partial Regex QuotedLiteral();
}
