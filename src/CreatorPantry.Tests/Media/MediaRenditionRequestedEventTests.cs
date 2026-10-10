using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The rendition request's payload: identifiers only, and read back exactly or not at all.
/// </summary>
public sealed class MediaRenditionRequestedEventTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    public static TheoryData<MediaRenditionSource> Sources() => new()
    {
        MediaRenditionSource.ForGeneratedImage(Guid.NewGuid()),
        MediaRenditionSource.ForAssetVersion(Guid.NewGuid(), 3),
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void A_request_reads_back_as_the_picture_it_was_written_for(MediaRenditionSource source)
    {
        var written = MediaRenditionRequestedEvent.For(Workspace, source);

        var read = MediaRenditionRequestedEvent.TryParse(written.Serialize());

        Assert.Equal(written, read);
        Assert.Equal(source, read!.Source);
        Assert.Equal(source.GeneratedImageId ?? source.MediaAssetId, read.CorrelationId);
    }

    [Fact]
    public void The_payload_carries_identifiers_and_nothing_derived_from_them()
    {
        var json = MediaRenditionRequestedEvent.For(Workspace, MediaRenditionSource.ForAssetVersion(Guid.NewGuid(), 1)).Serialize();

        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(
            ["generatedImageId", "mediaAssetId", "mediaAssetVersionNumber", "workspaceId"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"workspaceId":"00000000-0000-0000-0000-000000000000","generatedImageId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"workspaceId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"workspaceId":"11111111-1111-1111-1111-111111111111","mediaAssetId":"22222222-2222-2222-2222-222222222222"}""")]
    [InlineData("""{"workspaceId":"11111111-1111-1111-1111-111111111111","mediaAssetId":"22222222-2222-2222-2222-222222222222","mediaAssetVersionNumber":0}""")]
    [InlineData("""{"workspaceId":"11111111-1111-1111-1111-111111111111","generatedImageId":"22222222-2222-2222-2222-222222222222","mediaAssetId":"33333333-3333-3333-3333-333333333333","mediaAssetVersionNumber":1}""")]
    public void A_payload_that_does_not_name_exactly_one_picture_in_a_workspace_is_not_a_request(string payload)
    {
        Assert.Null(MediaRenditionRequestedEvent.TryParse(payload));
    }
}
