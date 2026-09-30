using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a client sends to ask for an editorial package for one approved recipe version (RCPUB-001).
/// </summary>
/// <remarks>
/// There is no scope, no model, no brand field and no workspace: the scope is fixed server-side, the model is
/// the deployment's, the brand facts are read from the workspace's own profile, and the workspace is the route's.
/// </remarks>
public sealed class RequestEditorialPackageViewModel
{
    /// <summary>The exact recipe version to write about. Must be the recipe's current, approved version.</summary>
    public Guid SourceVersionId { get; set; }

    /// <summary>
    /// The sections to write, by name (<c>headnote</c>, <c>introduction</c>, <c>tips</c>, <c>substitutions</c>,
    /// <c>storageReheating</c>, <c>faq</c>, <c>cta</c>). Omitted or empty means all seven.
    /// </summary>
    public List<string>? Sections { get; set; }
}

public sealed class RequestEditorialPackageViewModelValidator : AbstractValidator<RequestEditorialPackageViewModel>
{
    public RequestEditorialPackageViewModelValidator()
    {
        RuleFor(model => model.SourceVersionId)
            .NotEmpty().WithMessage("Name the recipe version to write about.");

        RuleFor(model => model.Sections)
            .Must(sections => sections is null
                || sections.All(section => AiEditorialSectionCatalog.Parse(section) is not null))
            .WithMessage("Each section must be one of headnote, introduction, tips, substitutions, storageReheating, faq or cta.")
            .Must(sections => sections is null
                || sections.Select(section => AiEditorialSectionCatalog.Parse(section)).Distinct().Count() == sections.Count)
            .WithMessage("Name each section once.");
    }
}

public static class AiEditorialPackageRequestErrors
{
    public const string RequestInvalid = "ai.editorialPackage.invalid_request";

    public const string TaskNotEnabled = "ai.editorialPackage.not_enabled";

    public const string RecipeNotFound = "ai.editorialPackage.recipe.not_found";

    public const string SourceVersionInvalid = "ai.editorialPackageSource.invalid_request";

    public const string RecipeNotApproved = "ai.editorialPackage.recipe.not_approved";

    public const string RequestNotFound = "ai.editorialPackageRequest.not_found";
}
