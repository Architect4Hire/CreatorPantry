using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// Staging a generated image asks for its renditions in the same commit, and waits for nothing (AF.5.5).
/// </summary>
public sealed partial class GeneratedImageWorkerTests
{
    [Fact]
    public async Task Every_staged_image_has_a_rendition_request_written_with_it()
    {
        var requested = await RequestAsync(WorkspaceA, variants: 3);

        var summary = await RunAsync();

        // Generation finished on its own: nothing here dispatched, decoded or encoded anything.
        Assert.Equal(1, summary.Generated);

        var images = await ImagesAsync(WorkspaceA, requested.Id);
        Assert.Equal(3, images.Count);

        await using var scope = _provider.CreateAsyncScope();
        var requests = (await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .OutboxMessages
                .AsNoTracking()
                .Where(message => message.Type == MediaRenditionRequestedEvent.MessageType)
                .ToListAsync(TestContext.Current.CancellationToken))
            .Select(message => MediaRenditionRequestedEvent.TryParse(message.PayloadJson))
            .ToList();

        // One per image, naming the workspace the operation belongs to and nothing but identifiers.
        Assert.Equal(
            images.Select(image => image.Id).Order(),
            requests.Select(request => request!.GeneratedImageId!.Value).Order());
        Assert.All(requests, request =>
        {
            Assert.Equal(WorkspaceA, request!.WorkspaceId);
            Assert.Null(request.MediaAssetId);
        });
    }
}
