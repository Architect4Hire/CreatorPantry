using Microsoft.Extensions.AI;

namespace CreatorPantry.Domain.Managers.Ai;

// MEAI001: IImageGenerator and its request/response types are marked experimental in
// Microsoft.Extensions.AI.Abstractions 10.10.1. Suppressed here rather than project-wide, and narrowly, so
// that a future experimental API elsewhere still has to be opted into deliberately. The alternative was
// inventing a parallel image abstraction of our own, which would have to be mapped onto this one the moment
// a real deployment arrives — a provider-neutral seam that is not the ecosystem's provider-neutral seam.
// If this type changes shape, the files that name it are this one, the gateway, and the registration helper.
#pragma warning disable MEAI001

/// <summary>
/// The image generator a host registers when no image deployment is configured.
/// </summary>
/// <remarks>
/// <para>
/// Every call throws <see cref="AiProviderNotConfiguredException"/>, for the same reason
/// <see cref="UnconfiguredEmbeddingGenerator"/> does: a placeholder answer would be worse than no answer.
/// There is no harmless image to return — a blank or a stand-in would be staged, checksummed and offered to
/// a creator as something a model produced.
/// </para>
/// <para>
/// <strong>A host with no <c>images</c> deployment registers this</strong> — in any environment, because a
/// missing image deployment turns one feature off rather than the product (B-15 as amended by 12.10c-1). The
/// generation job then settles every operation as <c>ProviderNotConfigured</c>: a terminal, accurately
/// recorded failure rather than a hang. A host that has one registers <c>VeniceImageGenerator</c> from
/// <c>CreatorPantry.AiProvider</c> instead, and the job, the gateway and their tests are the parts that did
/// not have to change for it.
/// </para>
/// </remarks>
public sealed class UnconfiguredImageGenerator : IImageGenerator
{
    public Task<ImageGenerationResponse> GenerateAsync(
        ImageGenerationRequest request,
        ImageGenerationOptions? options = null,
        CancellationToken cancellationToken = default) => throw Unconfigured();

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
    }

    public void Dispose()
    {
    }

    private static AiProviderNotConfiguredException Unconfigured() => new("images");
}
