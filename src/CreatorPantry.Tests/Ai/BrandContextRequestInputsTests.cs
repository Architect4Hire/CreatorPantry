using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// What a creator chose about brand context, as it survives the trip through an operation's stored inputs
/// (11A.20).
/// </summary>
public sealed class BrandContextRequestInputsTests
{
    private static readonly Guid Guide = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DocumentOne = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DocumentTwo = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void A_full_selection_survives_the_round_trip()
    {
        var selection = new BrandContextRequestSelection(
            UseBrandVoice: true,
            new BrandGuideSelection(Guide, 3),
            "weeknight cooks",
            [DocumentOne, DocumentTwo]);

        var read = Roundtrip(selection);

        Assert.NotNull(read);
        Assert.True(read.UseBrandVoice);
        Assert.Equal(Guide, read.Guide!.GuideId);
        Assert.Equal(3, read.Guide.VersionNumber);
        Assert.Equal("weeknight cooks", read.Audience);
        Assert.Equal(new[] { DocumentOne, DocumentTwo }, read.SourceDocumentIds);
    }

    /// <summary>
    /// The stored inputs say what the creator chose, not what the product defaulted to on the day they asked.
    /// </summary>
    [Fact]
    public void Defaults_are_not_written_at_all()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        BrandContextRequestInputs.Write(values, BrandContextRequestSelection.Default);

        Assert.Empty(values);
    }

    /// <summary>
    /// Off is the one non-default worth recording, and it records nothing else: a generation with brand voice off
    /// has no guide, no audience and no documents to name.
    /// </summary>
    [Fact]
    public void Brand_voice_off_is_recorded_and_carries_nothing_else()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        BrandContextRequestInputs.Write(
            values,
            new BrandContextRequestSelection(false, new BrandGuideSelection(Guide, 3), "ignored", [DocumentOne]));

        Assert.Equal("false", Assert.Single(values).Value);

        var read = BrandContextRequestInputs.Read(values);

        Assert.NotNull(read);
        Assert.False(read.UseBrandVoice);
        Assert.Null(read.Guide);
        Assert.Null(read.Audience);
        Assert.Empty(read.SourceDocumentIds);
    }

    /// <summary>
    /// An operation queued before this seam existed carries none of these keys, and must read back as the default
    /// a creator who never saw the control would have got. Nothing re-interprets an old request as an opt-out.
    /// </summary>
    [Fact]
    public void An_operation_from_before_this_seam_reads_as_brand_voice_on()
    {
        var inFlight = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sections"] = "headnote,introduction",
            ["brandName"] = "Sam's Kitchen",
            ["audience"] = "home cooks",
        };

        var read = BrandContextRequestInputs.Read(inFlight);

        Assert.NotNull(read);
        Assert.True(read.UseBrandVoice);
        Assert.Null(read.Guide);
        Assert.Empty(read.SourceDocumentIds);

        // The old key is left where it was rather than read as this seam's audience. Two meanings for one key is
        // the overlap that is invisible until a replay.
        Assert.Null(read.Audience);
    }

    [Fact]
    public void No_inputs_at_all_read_as_the_default()
    {
        var read = BrandContextRequestInputs.Read(null);

        Assert.NotNull(read);
        Assert.True(read.UseBrandVoice);
        Assert.Null(read.Guide);
    }

    /// <summary>
    /// Half a guide selection is refused rather than resolved to the active guide, which would silently hand back
    /// a version the creator did not ask for.
    /// </summary>
    [Theory]
    [InlineData(BrandContextRequestInputs.BrandGuideId, "11111111-1111-1111-1111-111111111111")]
    [InlineData(BrandContextRequestInputs.BrandGuideVersionNumber, "3")]
    public void Half_a_guide_selection_is_unreadable(string key, string value)
    {
        Assert.Null(BrandContextRequestInputs.Read(
            new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value }));
    }

    /// <summary>
    /// A value this server wrote that cannot be parsed is a defect, not bad creator input — the edge validated the
    /// request long before it reached here. The handler turns it into a validation failure rather than generating
    /// without the context the creator asked for.
    /// </summary>
    [Theory]
    [InlineData("not-a-guid", "3")]
    [InlineData("00000000-0000-0000-0000-000000000000", "3")]
    [InlineData("11111111-1111-1111-1111-111111111111", "zero")]
    [InlineData("11111111-1111-1111-1111-111111111111", "0")]
    [InlineData("11111111-1111-1111-1111-111111111111", "-2")]
    public void An_unparseable_guide_selection_is_unreadable(string guideId, string versionNumber)
    {
        Assert.Null(BrandContextRequestInputs.Read(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BrandContextRequestInputs.BrandGuideId] = guideId,
            [BrandContextRequestInputs.BrandGuideVersionNumber] = versionNumber,
        }));
    }

    [Fact]
    public void An_unparseable_document_list_is_unreadable()
    {
        Assert.Null(BrandContextRequestInputs.Read(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BrandContextRequestInputs.SourceDocumentIds] = $"{DocumentOne},not-a-guid",
        }));
    }

    /// <summary>
    /// No channel on any writing task built so far. The channel catalogue holds social channels, and a blog
    /// package, SEO metadata, a recipe concept and a first draft are none of them — so a channel here could only
    /// pull a social variant over long-form guidance. The social task is the one that will set it.
    /// </summary>
    [Fact]
    public void A_request_names_no_channel()
    {
        var request = BrandContextRequestSelection.Default.ToRequest(AiTaskType.EditorialPackage);

        Assert.NotNull(request);
        Assert.Null(request.ChannelKey);
        Assert.Equal(AiTaskType.EditorialPackage, request.TaskType);
    }

    /// <summary>
    /// Brand voice off produces no request at all, which is how a handler knows to pass no package and record no
    /// provenance — keeping "I turned it off" distinguishable from "it had nothing to give".
    /// </summary>
    [Fact]
    public void Brand_voice_off_produces_no_request()
    {
        Assert.Null(BrandContextRequestSelection.Off.ToRequest(AiTaskType.SeoPackage));
    }

    [Fact]
    public void A_request_carries_the_guide_the_audience_and_the_documents()
    {
        var request = new BrandContextRequestSelection(
                true, new BrandGuideSelection(Guide, 3), "weeknight cooks", [DocumentOne])
            .ToRequest(AiTaskType.RecipeFirstDraft);

        Assert.NotNull(request);
        Assert.Equal(new BrandGuideSelection(Guide, 3), request.Guide);
        Assert.Equal("weeknight cooks", request.Audience);
        Assert.Equal(new[] { DocumentOne }, request.SourceDocumentIds);
    }

    private static BrandContextRequestSelection? Roundtrip(BrandContextRequestSelection selection)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        BrandContextRequestInputs.Write(values, selection);

        return BrandContextRequestInputs.Read(values);
    }
}
