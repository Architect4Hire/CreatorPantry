using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The shape rules for a prompt save: what a client is told before anything is read or written.
/// </summary>
/// <remarks>
/// These mirror <c>PromptRecordConfiguration</c>'s check constraints, so most of them are also enforced by the
/// engine (<c>PromptRecordSqlServerTests</c>) and by EF (<c>PromptRecordAggregateTests</c>). The point of
/// asking twice is the answer's shape: a creator gets a field error naming their field rather than a storage
/// exception. A rule dropped from here would still be caught, but only as a 500.
/// </remarks>
public sealed class PromptRecordValidatorTests
{
    private static readonly SavePromptRecordViewModelValidator Validator = new(new ContentChannelCatalog());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SavePromptRecordViewModel Manual() => new()
    {
        ChannelKey = "instagram",
        ImageKind = PromptImageKind.Hero,
        Text = "Overhead shot of soda bread on a linen cloth, soft window light.",
        Label = "Soda bread hero",
        Source = PromptRecordSource.Manual,
    };

    private static SavePromptRecordViewModel Generated() => Manual() with
    {
        Source = PromptRecordSource.ImagePromptComposition,
        GeneratedText = "Overhead shot of soda bread, window light.",
        AiProposalId = Guid.NewGuid(),
        PromptTemplateId = "image.prompt",
        PromptTemplateVersion = "1.0.0",
        PromptTemplateBodyChecksum = $"sha256:{new string('a', 64)}",
    };

    private static async Task<IReadOnlyList<string>> FailuresFor(SavePromptRecordViewModel model)
    {
        var result = await Validator.ValidateAsync(model, Ct);

        return [.. result.Errors.Select(failure => failure.PropertyName)];
    }

    [Fact]
    public async Task A_prompt_the_creator_wrote_is_accepted()
    {
        Assert.Empty(await FailuresFor(Manual()));
    }

    [Fact]
    public async Task A_prompt_a_model_drafted_is_accepted_with_its_whole_provenance()
    {
        Assert.Empty(await FailuresFor(Generated()));
    }

    [Fact]
    public async Task A_prompt_needs_some_text()
    {
        Assert.Contains("Text", await FailuresFor(Manual() with { Text = "   " }));
        Assert.Contains("Text", await FailuresFor(Manual() with { Text = null }));
    }

    [Fact]
    public async Task A_prompt_past_the_columns_length_is_refused_here_rather_than_by_the_column()
    {
        var text = new string('a', ContentPolicy.PromptTextMaxLength + 1);

        Assert.Contains("Text", await FailuresFor(Manual() with { Text = text }));
        Assert.Contains("GeneratedText", await FailuresFor(Generated() with { GeneratedText = text }));
    }

    [Fact]
    public async Task A_label_past_its_length_is_refused()
    {
        var label = new string('a', ContentPolicy.PromptLabelMaxLength + 1);

        Assert.Contains("Label", await FailuresFor(Manual() with { Label = label }));
    }

    [Fact]
    public async Task A_prompt_names_the_channel_its_image_is_for()
    {
        Assert.Contains("ChannelKey", await FailuresFor(Manual() with { ChannelKey = null }));
        Assert.Contains("ChannelKey", await FailuresFor(Manual() with { ChannelKey = "mastodon" }));
    }

    [Fact]
    public async Task A_retired_channel_cannot_be_newly_chosen()
    {
        // The same bar the brand profile applies: a record that already stores a retired key keeps resolving it,
        // but nothing new may point at one.
        var validator = new SavePromptRecordViewModelValidator(new ContentChannelCatalog(
        [
            new ContentChannel("instagram", "Instagram"),
            new ContentChannel("vine", "Vine", IsActive: false),
        ]));

        var result = await validator.ValidateAsync(Manual() with { ChannelKey = "vine" }, Ct);

        Assert.Contains("ChannelKey", result.Errors.Select(failure => failure.PropertyName));
        Assert.True((await validator.ValidateAsync(Manual(), Ct)).IsValid);
    }

    [Fact]
    public async Task An_omitted_kind_or_source_is_refused_rather_than_defaulted()
    {
        // Hero and Manual are both 0, so an omission would otherwise arrive as a real answer.
        Assert.Contains("ImageKind", await FailuresFor(Manual() with { ImageKind = null }));
        Assert.Contains("Source", await FailuresFor(Manual() with { Source = null }));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task A_kind_or_source_outside_the_enum_is_refused(int value)
    {
        Assert.Contains("ImageKind", await FailuresFor(Manual() with { ImageKind = (PromptImageKind)value }));
        Assert.Contains("Source", await FailuresFor(Manual() with { Source = (PromptRecordSource)value }));
    }

    [Fact]
    public async Task A_prompt_the_creator_wrote_claims_no_proposal_template_or_draft()
    {
        Assert.Contains("AiProposalId", await FailuresFor(Manual() with { AiProposalId = Guid.NewGuid() }));
        Assert.Contains("GeneratedText", await FailuresFor(Manual() with { GeneratedText = "a draft" }));
        Assert.Contains("PromptTemplateId", await FailuresFor(Manual() with { PromptTemplateId = "image.prompt" }));
        Assert.Contains("PromptTemplateVersion", await FailuresFor(Manual() with { PromptTemplateVersion = "1.0.0" }));
        Assert.Contains(
            "PromptTemplateBodyChecksum",
            await FailuresFor(Manual() with { PromptTemplateBodyChecksum = "sha256:x" }));
    }

    [Fact]
    public async Task A_generated_prompt_names_its_proposal_draft_and_whole_template_triple()
    {
        Assert.Contains("AiProposalId", await FailuresFor(Generated() with { AiProposalId = null }));
        Assert.Contains("AiProposalId", await FailuresFor(Generated() with { AiProposalId = Guid.Empty }));
        Assert.Contains("GeneratedText", await FailuresFor(Generated() with { GeneratedText = null }));

        // A version string alone is a claim, so the checksum is not optional.
        Assert.Contains("PromptTemplateId", await FailuresFor(Generated() with { PromptTemplateId = null }));
        Assert.Contains("PromptTemplateVersion", await FailuresFor(Generated() with { PromptTemplateVersion = null }));
        Assert.Contains(
            "PromptTemplateBodyChecksum",
            await FailuresFor(Generated() with { PromptTemplateBodyChecksum = null }));
    }

    [Fact]
    public async Task A_version_pin_without_its_recipe_is_refused()
    {
        var failures = await FailuresFor(Manual() with { RecipeVersionId = Guid.NewGuid() });

        Assert.Contains("RecipeId", failures);
    }

    [Fact]
    public async Task A_recipe_pin_may_stand_without_a_version()
    {
        Assert.Empty(await FailuresFor(Manual() with { RecipeId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task An_empty_guid_is_not_an_id()
    {
        Assert.Contains("RecipeId", await FailuresFor(Manual() with { RecipeId = Guid.Empty }));
        Assert.Contains(
            "RecipeVersionId",
            await FailuresFor(Manual() with { RecipeId = Guid.NewGuid(), RecipeVersionId = Guid.Empty }));
    }

    /// <summary>
    /// A DAM asset may be named, now that something can resolve one.
    /// </summary>
    /// <remarks>
    /// <strong>This test asserted the field's <em>absence</em> until 12.9a.</strong> The column existed from
    /// 12.3, nothing could resolve an id, and the row is immutable — so a wrong value written once could
    /// only ever be erased. The DAM aggregate now exists, the id is resolved through
    /// <c>IMediaAssetLookupFacade</c> inside the resolved workspace, and the composite foreign key makes a
    /// neighbour's asset unrepresentable. Four parts of one change, and deleting the old assertion was the
    /// fourth — exactly as 12.6 did for <c>GeneratedImageId</c>.
    /// </remarks>
    [Fact]
    public void The_request_may_name_an_asset_this_server_can_resolve()
    {
        var properties = typeof(SavePromptRecordViewModel).GetProperties().Select(property => property.Name).ToList();

        Assert.Contains("DamAssetId", properties);
    }

    /// <summary>
    /// A generated image may be named, now that something can resolve one.
    /// </summary>
    /// <remarks>
    /// The other half of the line deleted above: the field's presence is as deliberate as its absence was, and
    /// a change that removed it again would otherwise pass unnoticed.
    /// </remarks>
    [Fact]
    public async Task A_generated_image_may_be_named()
    {
        Assert.Contains(
            "GeneratedImageId",
            typeof(SavePromptRecordViewModel).GetProperties().Select(property => property.Name));

        // Shape only: whether this workspace holds that image is the facade's question, and
        // PromptRecordBusinessTests is where it is asked.
        Assert.Empty(await FailuresFor(Manual() with { GeneratedImageId = Guid.NewGuid() }));
    }

    /// <summary>An empty Guid is a client that meant to send nothing, and is named rather than resolved.</summary>
    [Fact]
    public async Task An_empty_generated_image_id_is_refused_by_name()
    {
        Assert.Contains(
            "GeneratedImageId",
            await FailuresFor(Manual() with { GeneratedImageId = Guid.Empty }));
    }
}
