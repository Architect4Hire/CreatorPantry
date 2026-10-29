using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// The low-credit notice through the whole stack (AP.6.4, <c>ai-pricing.md</c> B-59): a picture takes an
/// account under the line, the creator's own usage read says so, and the two things they can do about it —
/// put it away, or stop being told — are theirs alone.
/// </summary>
/// <remarks>
/// Over the browser's cookie session and the gateway, so the camelCase names the web decoder reads are the
/// ones the API actually sends, rather than the ones a snapshot says it should.
/// </remarks>
public sealed partial class ImagesJourneyEndToEndTests
{
    private const string Notice = "/api/v1/me/ai-usage/low-credit-notice";

    [Fact]
    public async Task A_picture_that_leaves_an_account_low_tells_it_once_and_it_can_be_put_away()
    {
        // Twenty pictures' worth: four requests of four leave a fifth, which is not low, and the picture that
        // takes it to a tenth is the one that raises the notice.
        await AllowanceAsync(A.OwnerEmail, Credits(20));
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var before = (await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("lowCredit");
        Assert.False(before.GetProperty("isNoticed").GetBoolean());
        Assert.False(before.GetProperty("isDismissed").GetBoolean());
        Assert.True(before.GetProperty("noticeEnabled").GetBoolean());
        Assert.Equal(Credits(20), before.GetProperty("spendable").GetDecimal());

        // Sixteen of the twenty: a fifth left, which the nearly-spent warning covers and this does not.
        await AskForPicturesAsync(owner, 4, 4);

        var stillQuiet = (await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("lowCredit");
        Assert.False(stillQuiet.GetProperty("isNoticed").GetBoolean());
        Assert.Equal(Credits(4), stillQuiet.GetProperty("spendable").GetDecimal());

        // Two more take it to a tenth, which is the line.
        await AskForPicturesAsync(owner, 1, 2);

        var low = (await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("lowCredit");
        Assert.True(low.GetProperty("isNoticed").GetBoolean());
        Assert.False(low.GetProperty("isDismissed").GetBoolean());
        Assert.Equal(Credits(2), low.GetProperty("spendable").GetDecimal());

        // Put away, and it stays put away. 204 either time: a dismissal is the state the caller asked for.
        Assert.Equal(HttpStatusCode.NoContent, (await DismissAsync(owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DismissAsync(owner)).StatusCode);

        var dismissed = (await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("lowCredit");
        Assert.True(dismissed.GetProperty("isNoticed").GetBoolean());
        Assert.True(dismissed.GetProperty("isDismissed").GetBoolean());
    }

    /// <summary>
    /// The setting is the account's own, and it is the only thing a creator can change here: nothing on this
    /// route buys anything (PRICE-011 as amended).
    /// </summary>
    [Fact]
    public async Task The_notice_can_be_switched_off_and_on_again_by_its_owner()
    {
        await AllowanceAsync(A.OwnerEmail, Credits(10));
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync(Notice, new { enabled = false }, Ct)).StatusCode);

        var off = (await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("lowCredit");
        Assert.False(off.GetProperty("noticeEnabled").GetBoolean());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync(Notice, new { enabled = true }, Ct)).StatusCode);

        var on = (await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("lowCredit");
        Assert.True(on.GetProperty("noticeEnabled").GetBoolean());
    }

    /// <summary>
    /// A body that names no setting is refused rather than read as "off" — the whole reason the field is
    /// nullable. A stable code, and nothing of the account in the problem.
    /// </summary>
    [Fact]
    public async Task A_setting_that_says_nothing_is_refused_and_changes_nothing()
    {
        await AllowanceAsync(A.OwnerEmail, Credits(10));
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var refused = await owner.PutAsJsonAsync(Notice, new { }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await BodyOf(refused);
        Assert.Equal("ai.quota.notice_invalid", problem.GetProperty("code").GetString());
        Assert.Equal("none", problem.GetProperty("retry").GetString());

        var unchanged = (await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("lowCredit");
        Assert.True(unchanged.GetProperty("noticeEnabled").GetBoolean());
    }

    /// <summary>
    /// One account's notice is not another's, over the wire as well as in the tables: the route takes no
    /// account id, so the neighbour's own read is the only thing their session can reach (USAGE-008).
    /// </summary>
    [Fact]
    public async Task A_neighbour_sees_none_of_it()
    {
        await AllowanceAsync(A.OwnerEmail, Credits(20));
        await AllowanceAsync(A.MemberEmail, Credits(20));

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var colleague = await _fixture.SignInAsync(A.MemberEmail, cancellationToken: Ct);

        await AskForPicturesAsync(owner, 4, 4);
        await AskForPicturesAsync(owner, 1, 2);

        await DismissAsync(owner);
        await owner.PutAsJsonAsync(Notice, new { enabled = false }, Ct);

        // Same workspace, same moment, nothing of the owner's: not the notice, not the dismissal, not the setting.
        var theirs = (await BodyOf(await colleague.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("lowCredit");
        Assert.False(theirs.GetProperty("isNoticed").GetBoolean());
        Assert.False(theirs.GetProperty("isDismissed").GetBoolean());
        Assert.True(theirs.GetProperty("noticeEnabled").GetBoolean());
        Assert.Equal(Credits(20), theirs.GetProperty("spendable").GetDecimal());
    }

    /// <summary>
    /// Asks for <paramref name="each"/> pictures, <paramref name="times"/> times, and insists every request
    /// was accepted — a refusal part-way through would leave the balance somewhere this test did not aim for.
    /// </summary>
    private async Task AskForPicturesAsync(GatewayClient client, int times, int each)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            var asked = await client.PostAsJsonAsync(
                In(A, "generated-images"),
                new { promptText = Prompt, variantCount = each },
                Guid.NewGuid().ToString("N"),
                Ct);

            Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        }
    }

    /// <summary>The dismissal takes no body, so it goes through the sender that sends none.</summary>
    private static Task<HttpResponseMessage> DismissAsync(GatewayClient client) =>
        client.SendAsync(HttpMethod.Post, $"{Notice}/dismissal", body: null, headers: null, Ct);
}
