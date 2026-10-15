using System.ComponentModel;
using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>
/// The query string of <c>GET .../workflow-runs</c> (13A.11). Carries no workspace and no person: the workspace
/// comes from the route, and "mine" means the caller's own membership.
/// </summary>
public sealed record WorkflowRunListViewModel(
    [property: FromQuery(Name = "view")]
    [property: Description("Which runs: mine (in progress, started by or assigned to you — the default), completed, attention (in progress and in need of a look), or all.")]
    string? View = null,
    [property: FromQuery(Name = "definition")]
    [property: Description("One workflow by key, such as recipe.improve. A key no workflow has is an empty page rather than an error.")]
    string? Definition = null,
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace, the view, the filter and the reader it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);

/// <summary>Which runs a list holds.</summary>
public enum WorkflowRunListView
{
    /// <summary>In progress, and started by or assigned to the reader.</summary>
    Mine = 1,

    /// <summary>Completed, most recently first.</summary>
    Completed = 2,

    /// <summary>In progress with a reason to look at it the server can see from the run alone, oldest first.</summary>
    Attention = 3,

    /// <summary>Every run in the workspace, whatever its state.</summary>
    All = 4,
}

/// <summary>How each input of <see cref="WorkflowRunListViewModel"/> is read, for the validator and the factory alike.</summary>
public static class WorkflowRunListInputs
{
    /// <summary>Trimmed, and null when nothing is left.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>A view by name, in any case, and never by number — <c>3</c> would mean something to .NET and nothing to a reader.</summary>
    public static bool TryParseView(string value, out WorkflowRunListView view) =>
        Enum.TryParse(value, ignoreCase: true, out view)
        && Enum.IsDefined(view)
        && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
}

/// <summary>Shape and format validation for <see cref="WorkflowRunListViewModel"/>.</summary>
public sealed class WorkflowRunListViewModelValidator : AbstractValidator<WorkflowRunListViewModel>
{
    public WorkflowRunListViewModelValidator()
    {
        RuleFor(model => model.View)
            .Must(value => WorkflowRunListInputs.TryParseView(WorkflowRunListInputs.Normalize(value)!, out _))
            .WithMessage("Name a view: mine, completed, attention or all.")
            .When(model => WorkflowRunListInputs.Normalize(model.View) is not null)
            .OverridePropertyName("view");

        RuleFor(model => model.Definition)
            .Must(value => WorkflowRunListInputs.Normalize(value)!.Length <= WorkflowRunPolicy.KeyMaxLength)
            .WithMessage($"A workflow key is at most {WorkflowRunPolicy.KeyMaxLength} characters.")
            .When(model => WorkflowRunListInputs.Normalize(model.Definition) is not null)
            .OverridePropertyName("definition");

        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");
    }
}
