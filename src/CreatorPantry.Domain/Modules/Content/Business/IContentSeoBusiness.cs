using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

public interface IContentSeoBusiness
{
    Task<OperationResult<AcceptedSeoServiceModel?>> GetAcceptedSeoAsync(
        Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken);
}

internal sealed class ContentSeoBusiness(IContentSeoDataLayer dataLayer) : IContentSeoBusiness
{
    public async Task<OperationResult<AcceptedSeoServiceModel?>> GetAcceptedSeoAsync(
        Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken)
    {
        var accepted = await dataLayer.FindAcceptedAsync(recipeId, ContentPackageKind.Seo, cancellationToken);

        // Only the revision the creator currently has accepted can be named. An unknown recipe, another
        // workspace's recipe and a revision number that was never accepted all look the same from here.
        if (revisionNumber is { } requested && accepted?.RevisionNumber != requested)
        {
            return OperationResult<AcceptedSeoServiceModel?>.Failure(OperationError.Validation(
                ContentErrorCodes.RevisionNotFound,
                "That SEO revision could not be read.",
                [("seoRevision", "This recipe has no accepted SEO revision with that number.")]));
        }

        if (accepted is null)
        {
            return OperationResult<AcceptedSeoServiceModel?>.Success(null);
        }

        var isCurrent = AcceptedContentCurrency.IsCurrent(accepted, recipeVersionId);
        var (description, phrases) = SeoPackageDocumentReader.Read(accepted.Content);

        return OperationResult<AcceptedSeoServiceModel?>.Success(
            new AcceptedSeoServiceModel(accepted.RevisionNumber, description, phrases, isCurrent));
    }
}
