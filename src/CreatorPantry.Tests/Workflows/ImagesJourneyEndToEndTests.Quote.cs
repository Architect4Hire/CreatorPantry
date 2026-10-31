using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// What a picture will cost, shown before it is asked for (AP.2.6, PRICE-004), through the whole stack: the
/// browser's cookie session, the Gateway and the API — and checked against what the request then actually holds.
/// </summary>
public sealed partial class ImagesJourneyEndToEndTests
{
    private static string Quote(SeededWorkspace workspace, int variants) =>
        In(workspace, $"generated-images/quote?variants={variants}");

    /// <summary>
    /// The quote is the hold: what it says four pictures cost is exactly what asking for four then reserves, and
    /// what it says is left afterwards is what the creator's own usage then reads.
    /// </summary>
    [Fact]
    public async Task A_quote_prices_the_request_exactly_as_admission_then_holds_it()
    {
        await AllowanceAsync(A.OwnerEmail, Credits(10));
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var quote = await BodyOf(await owner.GetAsync(Quote(A, 4), Ct));

        Assert.Equal("Affordable", quote.GetProperty("outcome").GetString());
        Assert.Equal("Credits", quote.GetProperty("unit").GetString());
        Assert.Equal(4, quote.GetProperty("variants").GetInt32());
        Assert.Equal(Credits(1), quote.GetProperty("perPicture").GetDecimal());
        Assert.Equal(Credits(4), quote.GetProperty("total").GetDecimal());
        Assert.Equal(Credits(10), quote.GetProperty("allowance").GetDecimal());
        Assert.Equal(Credits(10), quote.GetProperty("remaining").GetDecimal());
        Assert.Equal(Credits(6), quote.GetProperty("remainingAfter").GetDecimal());
        Assert.Equal(4, quote.GetProperty("affordableVariants").GetInt32());
        Assert.False(quote.GetProperty("leavesNearlySpent").GetBoolean());
        Assert.True(quote.TryGetProperty("resetsAt", out _));
        Assert.Equal("Etc/UTC", quote.GetProperty("timeZoneId").GetString());

        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 4 }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        var usage = await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct));
        Assert.Equal(Credits(6), usage.GetProperty("period").GetProperty("remaining").GetDecimal());
    }

    /// <summary>
    /// A request the allowance cannot cover is quoted as such, with how many pictures it would cover — and that
    /// smaller request is one admission then accepts.
    /// </summary>
    [Fact]
    public async Task An_unaffordable_request_is_quoted_with_how_many_pictures_would_fit()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        await AllowanceAsync(A.OwnerEmail, Credits(2) + 5m);
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var quote = await BodyOf(await owner.GetAsync(Quote(A, 4), Ct));

        Assert.Equal("Exhausted", quote.GetProperty("outcome").GetString());
        Assert.Equal(Credits(4), quote.GetProperty("total").GetDecimal());
        Assert.Equal(2, quote.GetProperty("affordableVariants").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, quote.GetProperty("remainingAfter").ValueKind);

        var two = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 2 }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.Accepted, two.StatusCode);
    }

    /// <summary>
    /// Going ahead with a request that leaves a fifth or less is affordable and says so (USAGE-010) — the
    /// server's line, which the usage read then reports too.
    /// </summary>
    [Fact]
    public async Task A_request_that_leaves_the_allowance_nearly_spent_says_so_and_the_usage_read_agrees()
    {
        await AllowanceAsync(A.OwnerEmail, Credits(10));
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var quote = await BodyOf(await owner.GetAsync(Quote(A, 4), Ct));
        Assert.False(quote.GetProperty("leavesNearlySpent").GetBoolean());

        // Spend six of the ten first, so four more would leave nothing.
        var first = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 4 }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var second = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 2 }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        var last = await BodyOf(await owner.GetAsync(Quote(A, 4), Ct));
        Assert.Equal("Affordable", last.GetProperty("outcome").GetString());
        Assert.Equal(0m, last.GetProperty("remainingAfter").GetDecimal());
        Assert.True(last.GetProperty("leavesNearlySpent").GetBoolean());

        // Forty left of a hundred: two pictures leave twenty, exactly the line, which counts; one leaves thirty.
        Assert.True((await BodyOf(await owner.GetAsync(Quote(A, 2), Ct))).GetProperty("leavesNearlySpent").GetBoolean());
        Assert.False((await BodyOf(await owner.GetAsync(Quote(A, 1), Ct))).GetProperty("leavesNearlySpent").GetBoolean());

        // Four of ten left: 40%, not nearly spent; then the read and the quote are both the server's.
        var usage = await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct));
        Assert.False(usage.GetProperty("isNearlySpent").GetBoolean());
    }

    /// <summary>
    /// <strong>The quote is the asker's.</strong> A colleague in the same workspace who has spent nothing is quoted
    /// their own allowance, not the owner's — and a quote writes nothing, however often it is asked.
    /// </summary>
    [Fact]
    public async Task A_quote_reads_only_the_askers_own_allowance_and_writes_nothing()
    {
        await AllowanceAsync(A.OwnerEmail, Credits(1));
        await AllowanceAsync(A.MemberEmail, Credits(8));

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var colleague = await _fixture.SignInAsync(A.MemberEmail, cancellationToken: Ct);

        Assert.Equal("Exhausted", (await BodyOf(await owner.GetAsync(Quote(A, 4), Ct))).GetProperty("outcome").GetString());

        var theirs = await BodyOf(await colleague.GetAsync(Quote(A, 4), Ct));
        Assert.Equal("Affordable", theirs.GetProperty("outcome").GetString());
        Assert.Equal(Credits(8), theirs.GetProperty("remaining").GetDecimal());

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Quote(A, 4), Ct)).StatusCode);
        }

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Equal(0, await db.AccountAiQuotaReservations.CountAsync(Ct));
        Assert.Equal(0, await db.AccountAiQuotaPeriods.CountAsync(Ct));
    }

    /// <summary>
    /// A Viewer cannot ask for pictures, so has no price to see; a count a request would refuse cannot be quoted;
    /// and another workspace's route is not this caller's to price in.
    /// </summary>
    [Theory]
    [InlineData("viewer", 2, HttpStatusCode.Forbidden)]
    [InlineData("owner", 0, HttpStatusCode.BadRequest)]
    [InlineData("owner", 99, HttpStatusCode.BadRequest)]
    [InlineData("neighbour", 2, HttpStatusCode.NotFound)]
    public async Task A_quote_is_refused_as_the_request_it_prices_would_be(string who, int variants, HttpStatusCode expected)
    {
        var (email, workspace) = who switch
        {
            "viewer" => (B.MemberEmail, B),
            "neighbour" => (B.OwnerEmail, A),
            _ => (A.OwnerEmail, A),
        };

        using var client = await _fixture.SignInAsync(email, cancellationToken: Ct);

        Assert.Equal(expected, (await client.GetAsync(Quote(workspace, variants), Ct)).StatusCode);
    }
}
