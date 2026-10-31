using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// Today's pictures on the account's own usage read, through the browser's routes (DLIM-008, DL.9): one figure for
/// the account across every workspace it works in, never another account's, and the same cap and count the quote
/// refuses with.
/// </summary>
public sealed partial class ImagesJourneyEndToEndTests
{
    [Fact]
    public async Task Todays_pictures_are_the_accounts_across_its_workspaces_and_agree_with_the_quote()
    {
        OnDailyCapOf(5);
        await JoinAsync(A.OwnerEmail, B);
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var neighbour = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);

        var before = (await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("picturesToday");
        Assert.Equal(5, before.GetProperty("cap").GetInt32());
        Assert.Equal(0, before.GetProperty("used").GetInt32());
        Assert.Equal(5, before.GetProperty("left").GetInt32());

        // Two in each workspace: the day is the person's, not the workspace's.
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        await AskInAsync(owner, A, 2);
        await AskInAsync(owner, B, 2);
        await RunWorkerAsync();

        var today = (await BodyOf(await owner.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("picturesToday");
        Assert.Equal(5, today.GetProperty("cap").GetInt32());
        Assert.Equal(4, today.GetProperty("used").GetInt32());
        Assert.Equal(1, today.GetProperty("left").GetInt32());
        Assert.Equal(DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(1), today.GetProperty("resetsAt").GetDateTimeOffset().UtcDateTime);

        // The quote that refuses two more names the same cap and the same one left.
        var quote = await BodyOf(await owner.GetAsync(In(A, "generated-images/quote?variants=2"), Ct));
        Assert.Equal("DailyImageCap", quote.GetProperty("outcome").GetString());
        Assert.Equal(today.GetProperty("cap").GetInt32(), quote.GetProperty("pictureCap").GetInt32());
        Assert.Equal(today.GetProperty("left").GetInt32(), quote.GetProperty("picturesLeft").GetInt32());
        Assert.Equal(today.GetProperty("resetsAt").GetDateTimeOffset(), quote.GetProperty("pictureCapResetsAt").GetDateTimeOffset());

        // Another account working in B, where the owner of A made two, has used none of its own day.
        var theirs = (await BodyOf(await neighbour.GetAsync("/api/v1/me/ai-usage", Ct))).GetProperty("picturesToday");
        Assert.Equal(0, theirs.GetProperty("used").GetInt32());
        Assert.Equal(5, theirs.GetProperty("left").GetInt32());
    }

    private async Task AskInAsync(GatewayClient client, SeededWorkspace workspace, int variants)
    {
        var asked = await client.PostAsJsonAsync(
            In(workspace, "generated-images"),
            new { promptText = Prompt, variantCount = variants },
            Guid.NewGuid().ToString("N"),
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
    }

    /// <summary>Makes the person signed in as <paramref name="email"/> a Contributor in <paramref name="workspace"/> too.</summary>
    private async Task JoinAsync(string email, SeededWorkspace workspace)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync(Ct);

        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(Ct);
    }
}
