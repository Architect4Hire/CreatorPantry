using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// Reading one card's publications (13.10d): the order, the cap, and the three situations that are one answer.
/// </summary>
public sealed partial class PublishingScheduleTests
{
    [Fact]
    public async Task A_cards_publications_come_back_newest_scheduled_first()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_L1") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        var soonest = Confirmation(card.Id, profile.Id);
        var later = new SchedulePublishingViewModel
        {
            ContentCardId = card.Id,
            PublishingProfileId = profile.Id,
            Text = "A second post, later.",
            ScheduledAt = Now.AddDays(9),
            Confirmed = true,
        };

        var first = await ScheduleAsync(WsA, provider, soonest, NewKey());
        provider.Next = Accepted("update_EXTERNAL_L2");
        var second = await ScheduleAsync(WsA, provider, later, NewKey());

        var listed = await ListAsync(WsA, provider, card.Id);

        Assert.True(listed.Complete);
        Assert.Equal(
            [second.Value!.Record.Id, first.Value!.Record.Id],
            listed.Items.Select(item => item.Id));

        // Each item is what the confirm route returns, with the same silences.
        Assert.DoesNotContain("EXTERNAL", System.Text.Json.JsonSerializer.Serialize(listed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_card_with_nothing_scheduled_reads_as_an_empty_list()
    {
        var card = await SeedCardAsync(WsA);

        var listed = await ListAsync(WsA, new ScriptedProvider(WsA), card.Id);

        Assert.Empty(listed.Items);
        Assert.True(listed.Complete);
    }

    [Fact]
    public async Task Another_workspaces_card_reads_as_an_empty_list_rather_than_a_refusal()
    {
        var provider = new ScriptedProvider(WsA) { Next = Accepted("update_EXTERNAL_L3") };
        var card = await SeedCardAsync(WsA);
        var profile = await SeedProfileAsync(WsA, provider.Key);

        await ScheduleAsync(WsA, provider, Confirmation(card.Id, profile.Id), NewKey());

        // The same answer a card nobody has gets, and the same an empty card gets: asking about an id cannot
        // reveal that a neighbour owns it.
        Assert.Empty((await ListAsync(WsB, provider, card.Id)).Items);
        Assert.Empty((await ListAsync(WsA, provider, Guid.NewGuid())).Items);
        Assert.Empty((await ListAsync(WsA, provider, Guid.Empty)).Items);
    }

    private async Task<PublishingRecordListServiceModel> ListAsync(
        Guid workspaceId, ScriptedProvider provider, Guid cardId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return await Business(
                scope,
                provider,
                new MovableClock(),
                Zone,
                CreatorPantry.Domain.Modules.Media.Managers.MediaAssetLinkTarget.Linkable)
            .ListForCardAsync(cardId, Ct);
    }
}
