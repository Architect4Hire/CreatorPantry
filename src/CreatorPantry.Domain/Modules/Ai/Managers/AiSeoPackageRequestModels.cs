using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a client sends to ask for an SEO package for one approved recipe version (RCPUB-002).
/// </summary>
/// <remarks>
/// There is no scope, model, rule, brand or workspace field: the scope is fixed server-side, the model is the
/// deployment's, the length rules are configuration pinned at request time, the brand facts are read from the
/// workspace's own profile, and the workspace is the route's.
/// </remarks>
public sealed class RequestSeoPackageViewModel
{
    /// <summary>The exact recipe version to write about. Must be the recipe's current, approved version.</summary>
    public Guid SourceVersionId { get; set; }

    /// <summary>
    /// The sections to write, by name (<c>seoTitle</c>, <c>metaDescription</c>, <c>keyPhrases</c>, <c>slug</c>,
    /// <c>altText</c>, <c>internalLinks</c>). Omitted or empty means all six. A request for the slug alone is
    /// refused: a slug is derived from a title, and there is nothing for the model to write.
    /// </summary>
    public List<string>? Sections { get; set; }
}

public sealed class RequestSeoPackageViewModelValidator : AbstractValidator<RequestSeoPackageViewModel>
{
    public RequestSeoPackageViewModelValidator()
    {
        RuleFor(model => model.SourceVersionId)
            .NotEmpty().WithMessage("Name the recipe version to write about.");

        RuleFor(model => model.Sections)
            .Must(sections => sections is null
                || sections.All(section => AiSeoSectionCatalog.Parse(section) is not null))
            .WithMessage("Each section must be one of seoTitle, metaDescription, keyPhrases, slug, altText or internalLinks.")
            .Must(sections => sections is null
                || sections.Select(section => AiSeoSectionCatalog.Parse(section)).Distinct().Count() == sections.Count)
            .WithMessage("Name each section once.")
            .Must(sections => sections is null or { Count: 0 }
                || sections.Any(section => AiSeoSectionCatalog.Parse(section) is { } parsed && parsed is not AiSeoSection.Slug))
            .WithMessage("Name at least one section besides the slug; the slug is derived from a title.");
    }
}

public static class AiSeoPackageRequestErrors
{
    public const string RequestInvalid = "ai.seoPackage.invalid_request";

    public const string TaskNotEnabled = "ai.seoPackage.not_enabled";

    public const string RecipeNotFound = "ai.seoPackage.recipe.not_found";

    public const string SourceVersionInvalid = "ai.seoPackageSource.invalid_request";

    public const string RecipeNotApproved = "ai.seoPackage.recipe.not_approved";

    public const string RequestNotFound = "ai.seoPackageRequest.not_found";
}
