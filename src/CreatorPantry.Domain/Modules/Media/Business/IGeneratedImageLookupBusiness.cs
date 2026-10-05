using CreatorPantry.Domain.Modules.Media.Data;

namespace CreatorPantry.Domain.Modules.Media.Business;

/// <summary>Resolves a generated-image id another module is about to store, inside the resolved workspace.</summary>
public interface IGeneratedImageLookupBusiness
{
    /// <inheritdoc cref="IGeneratedImageRepository.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageLookupBusiness"/>
internal sealed class GeneratedImageLookupBusiness(IGeneratedImageDataLayer images)
    : IGeneratedImageLookupBusiness
{
    public Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        generatedImageId == Guid.Empty
            // Nothing to ask the database: an empty id was never issued, so it is no image of this workspace
            // or of any other. Answered here so a caller's missing value costs no query, exactly as
            // AiProposalLookupBusiness does.
            ? Task.FromResult(false)
            : images.ExistsAsync(generatedImageId, cancellationToken);
}
