using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>The query of a Markdown export. Carries no workspace and no recipe: the route and the resolved context name those.</summary>
public sealed record RecipeMarkdownExportViewModel(
    [property: FromQuery(Name = "versionNumber")]
    [property: Description("Optional. The version number to export, as the history lists it. Omitted means the recipe's current version.")]
    int? VersionNumber = null,
    [property: FromQuery(Name = "template")]
    [property: Description("Optional. The layout: standard (the default) or compact. Compact leaves out the description, equipment and editorial sections.")]
    string? Template = null,
    [property: FromQuery(Name = "units")]
    [property: Description("Optional. How quantities are shown: asWritten (the default), metric or usCustomary. A converted quantity is appended to the creator's own line, which is never replaced.")]
    string? Units = null,
    [property: FromQuery(Name = "editorialRevision")]
    [property: Description("Optional. The accepted editorial revision to include. Omitted means the accepted one, if it is current for the version. Ignored by the compact template.")]
    int? EditorialRevision = null)
{
    public static bool TryParseTemplate(string? value, out RecipeExportTemplate template)
    {
        template = RecipeExportTemplate.Standard;
        return string.IsNullOrEmpty(value) || Enum.TryParse(value, ignoreCase: true, out template) && Enum.IsDefined(template) && !int.TryParse(value, out _);
    }

    public static bool TryParseUnits(string? value, out RecipeUnitPresentation units)
    {
        units = RecipeUnitPresentation.AsWritten;
        return string.IsNullOrEmpty(value) || Enum.TryParse(value, ignoreCase: true, out units) && Enum.IsDefined(units) && !int.TryParse(value, out _);
    }
}

public sealed class RecipeMarkdownExportViewModelValidator : AbstractValidator<RecipeMarkdownExportViewModel>
{
    public RecipeMarkdownExportViewModelValidator()
    {
        RuleFor(model => model.VersionNumber)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Version numbers start at 1.")
            .When(model => model.VersionNumber is not null)
            .OverridePropertyName("versionNumber");

        RuleFor(model => model.EditorialRevision)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Revision numbers start at 1.")
            .When(model => model.EditorialRevision is not null)
            .OverridePropertyName("editorialRevision");

        RuleFor(model => model.Template)
            .Must(value => RecipeMarkdownExportViewModel.TryParseTemplate(value, out _))
            .WithMessage("Use standard or compact.")
            .OverridePropertyName("template");

        RuleFor(model => model.Units)
            .Must(value => RecipeMarkdownExportViewModel.TryParseUnits(value, out _))
            .WithMessage("Use asWritten, metric or usCustomary.")
            .OverridePropertyName("units");
    }
}

/// <summary>
/// A generated Markdown export. <see cref="Markdown"/> is the whole body and nothing else.
/// </summary>
/// <param name="VersionNumber">The version the document describes.</param>
/// <param name="FileName">A safe, deterministic ASCII file name for saving it. Never a path.</param>
/// <param name="Warnings">Facts left out or left as written without blocking the export, by stable code.</param>
public sealed record RecipeMarkdownExportServiceModel(
    string Markdown, int VersionNumber, string FileName, IReadOnlyList<RecipeMarkdownIssue> Warnings);
