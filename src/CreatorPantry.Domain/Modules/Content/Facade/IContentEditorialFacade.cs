using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// Reads a recipe's accepted editorial revision for another module's export. Read-only, and it resolves the
/// workspace from the request's own context: no caller can name one.
/// </summary>
public interface IContentEditorialFacade
{
    /// <param name="recipeId">The recipe whose accepted editorial revision to read.</param>
    /// <param name="recipeVersionId">The version the export describes, against which currency is judged.</param>
    /// <param name="revisionNumber">
    /// Optional. When given it must be the accepted revision's own number, else
    /// <c>content.revision.not_found</c>.
    /// </param>
    /// <returns>
    /// Success with <c>null</c> when nothing was ever accepted and no revision was asked for; a model whose
    /// <c>IsCurrent</c> is false when the acceptance is stale.
    /// </returns>
    Task<OperationResult<AcceptedEditorialServiceModel?>> GetAcceptedEditorialAsync(
        Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken);
}

internal sealed class ContentEditorialFacade(IContentEditorialBusiness business) : IContentEditorialFacade
{
    public Task<OperationResult<AcceptedEditorialServiceModel?>> GetAcceptedEditorialAsync(
        Guid recipeId, Guid recipeVersionId, int? revisionNumber, CancellationToken cancellationToken) =>
        business.GetAcceptedEditorialAsync(recipeId, recipeVersionId, revisionNumber, cancellationToken);
}
