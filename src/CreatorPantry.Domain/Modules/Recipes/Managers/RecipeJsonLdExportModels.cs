using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>The query of a JSON-LD export. Carries no workspace and no recipe: the route and the resolved context name those.</summary>
public sealed record RecipeJsonLdExportViewModel(
    [property: FromQuery(Name = "versionNumber")]
    [property: Description("Optional. The version number to export, as the history lists it. Omitted means the recipe's current version.")]
    int? VersionNumber = null,
    [property: FromQuery(Name = "seoRevision")]
    [property: Description("Optional. The accepted SEO revision to draw the description and keywords from. Omitted means the accepted one, if it is current for the version.")]
    int? SeoRevision = null);

public sealed class RecipeJsonLdExportViewModelValidator : AbstractValidator<RecipeJsonLdExportViewModel>
{
    public RecipeJsonLdExportViewModelValidator()
    {
        RuleFor(model => model.VersionNumber)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Version numbers start at 1.")
            .When(model => model.VersionNumber is not null)
            .OverridePropertyName("versionNumber");

        RuleFor(model => model.SeoRevision)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Revision numbers start at 1.")
            .When(model => model.SeoRevision is not null)
            .OverridePropertyName("seoRevision");
    }
}

/// <summary>The archived version an export reads, already deserialized and already known to be exportable.</summary>
public sealed record RecipeExportSource(
    Guid VersionId, int VersionNumber, RecipeVersionReadiness Readiness, RecipeSnapshotDocument Document);

/// <summary>
/// A generated JSON-LD document. <see cref="Json"/> is the whole body and nothing else, so it can be served or
/// embedded as it is; everything a client may want to know about it is alongside.
/// </summary>
/// <param name="VersionNumber">The version the document describes.</param>
/// <param name="Warnings">Facts omitted without blocking the export, by stable code.</param>
public sealed record RecipeJsonLdExportServiceModel(
    string Json, int VersionNumber, IReadOnlyList<RecipeJsonLdIssue> Warnings);
