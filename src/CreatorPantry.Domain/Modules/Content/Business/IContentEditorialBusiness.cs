using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

public interface IContentEditorialBusiness
{
    Task<OperationResult<AcceptedEditorialServiceModel?>> GetAcceptedEditorialAsync(
        Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken);
}

internal sealed class ContentEditorialBusiness(IContentSeoDataLayer dataLayer) : IContentEditorialBusiness
{
    public async Task<OperationResult<AcceptedEditorialServiceModel?>> GetAcceptedEditorialAsync(
        Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken)
    {
        var accepted = await dataLayer.FindAcceptedAsync(recipeId, ContentPackageKind.Editorial, cancellationToken);

        // Only the revision the creator currently has accepted can be named. An unknown recipe, another
        // workspace's recipe and a revision number that was never accepted all look the same from here.
        if (revisionNumber is { } requested && accepted?.RevisionNumber != requested)
        {
            return OperationResult<AcceptedEditorialServiceModel?>.Failure(OperationError.Validation(
                ContentErrorCodes.RevisionNotFound,
                "That editorial revision could not be read.",
                [("editorialRevision", "This recipe has no accepted editorial revision with that number.")]));
        }

        if (accepted is null)
        {
            return OperationResult<AcceptedEditorialServiceModel?>.Success(null);
        }

        return OperationResult<AcceptedEditorialServiceModel?>.Success(EditorialPackageDocumentReader.Read(
            accepted.RevisionNumber, accepted.Content, AcceptedContentCurrency.IsCurrent(accepted, recipeVersionId)));
    }
}
