namespace CreatorPantry.Domain.Modules.Media.Data;

/// <summary>Composes the generated-image workspace's persistence operations.</summary>
public interface IGeneratedImageDataLayer
{
    /// <inheritdoc cref="IGeneratedImageRepository.ExistsAsync"/>
    Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageDataLayer"/>
/// <remarks>
/// A single read, so there is nothing to compose and no transaction to own — the layer exists because the
/// seam does, and because a Business layer reaching the repository directly would be the defect backend.md
/// names.
/// </remarks>
internal sealed class GeneratedImageDataLayer(IGeneratedImageRepository images) : IGeneratedImageDataLayer
{
    public Task<bool> ExistsAsync(Guid generatedImageId, CancellationToken cancellationToken) =>
        images.ExistsAsync(generatedImageId, cancellationToken);
}
