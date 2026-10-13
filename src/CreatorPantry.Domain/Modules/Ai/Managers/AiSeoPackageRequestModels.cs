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

/// <summary>
/// What a client sends to ask for an SEO package for an accepted long-form draft (LF.5.3, LFC-007).
/// </summary>
/// <remarks>
/// The same capability as <see cref="RequestSeoPackageViewModel"/> with a different subject, named by the
/// route. There is no draft, scope, model, rule, brand or workspace field, for the same reasons.
/// </remarks>
public sealed class RequestLongFormSeoPackageViewModel
{
    /// <summary>The revision to write about. Must be the revision of the draft its creator accepted.</summary>
    public int RevisionNumber { get; set; }

    /// <summary>
    /// The sections to write, by name (<c>seoTitle</c>, <c>metaDescription</c>, <c>keyPhrases</c>, <c>slug</c>,
    /// <c>internalLinks</c>). Omitted or empty means all five. There is no <c>altText</c>: an article carries
    /// no image the package has been given any fact about. A request for the slug alone is refused.
    /// </summary>
    public List<string>? Sections { get; set; }
}

public sealed class RequestLongFormSeoPackageViewModelValidator : AbstractValidator<RequestLongFormSeoPackageViewModel>
{
    public RequestLongFormSeoPackageViewModelValidator()
    {
        RuleFor(model => model.RevisionNumber)
            .GreaterThanOrEqualTo(1).WithMessage("Name the accepted revision to write about.");

        RuleFor(model => model.Sections)
            .Must(sections => sections is null
                || sections.All(section => AiSeoSubjects.Offers(AiSeoSubject.Article, AiSeoSectionCatalog.Parse(section))))
            .WithMessage("Each section must be one of seoTitle, metaDescription, keyPhrases, slug or internalLinks.")
            .Must(sections => sections is null
                || sections.Select(section => AiSeoSectionCatalog.Parse(section)).Distinct().Count() == sections.Count)
            .WithMessage("Name each section once.")
            .Must(sections => sections is null or { Count: 0 }
                || sections.Any(section => AiSeoSectionCatalog.Parse(section) is { } parsed && parsed is not AiSeoSection.Slug))
            .WithMessage("Name at least one section besides the slug; the slug is derived from a title.");
    }
}

/// <summary>What an SEO package is written about. One capability; the route a request arrives on says which.</summary>
public enum AiSeoSubject
{
    /// <summary>One approved recipe version (RCPUB-002).</summary>
    Recipe = 0,

    /// <summary>One accepted long-form draft (LF.5.3).</summary>
    Article = 1,
}

public static class AiSeoSubjects
{
    /// <summary>As the PREFERENCES segment names the subject to the model.</summary>
    public static string ToWire(AiSeoSubject subject) => subject is AiSeoSubject.Article ? "article" : "recipe";

    /// <summary>
    /// The sections a subject can be asked for. An article is offered no alt text: it has no asset links, so
    /// there is no image the package has been given a caption or a title for (ai.md).
    /// </summary>
    public static IReadOnlyList<AiSeoSection> SectionsFor(AiSeoSubject subject) => subject is AiSeoSubject.Article
        ? [.. AiSeoSectionCatalog.All.Where(section => section is not AiSeoSection.AltText)]
        : AiSeoSectionCatalog.All;

    public static bool Offers(AiSeoSubject subject, AiSeoSection? section) =>
        section is { } named && SectionsFor(subject).Contains(named);
}

public static class AiSeoPackageRequestErrors
{
    public const string RequestInvalid = "ai.seoPackage.invalid_request";

    public const string TaskNotEnabled = "ai.seoPackage.not_enabled";

    public const string RecipeNotFound = "ai.seoPackage.recipe.not_found";

    public const string SourceVersionInvalid = "ai.seoPackageSource.invalid_request";

    public const string RecipeNotApproved = "ai.seoPackage.recipe.not_approved";

    public const string RequestNotFound = "ai.seoPackageRequest.not_found";

    /// <summary>No such long-form draft here — which is also the answer for another workspace's (LF.5.3).</summary>
    public const string DraftNotFound = "ai.seoPackage.longFormDraft.not_found";

    /// <summary>The draft is not accepted, or was accepted and its sources have since moved.</summary>
    public const string DraftNotAccepted = "ai.seoPackage.longFormDraft.not_accepted.conflict";

    /// <summary>The revision named is not the one the creator accepted.</summary>
    public const string DraftRevisionInvalid = "ai.seoPackageLongFormRevision.invalid_request";
}
