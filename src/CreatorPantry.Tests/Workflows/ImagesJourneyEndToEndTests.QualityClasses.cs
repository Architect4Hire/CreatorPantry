using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Tests.Brand;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// Quality classes through the whole stack (AP.3.1): the browser's session, the Gateway and the API, against the
/// shipped class mapping and rate table — with the default class pointed at the fake model, as
/// <see cref="TestDatabase"/> does for every host.
/// </summary>
public sealed partial class ImagesJourneyEndToEndTests
{
    /// <summary>
    /// <strong>The price list is the charging table</strong> (B-55, PRICE-013). Every class the route lists costs
    /// exactly what this host's admission charges for what that class sends — asked of the same host's own
    /// pricing, class by class — in order, with the default marked and no model named.
    /// </summary>
    [Fact]
    public async Task The_classes_route_lists_each_class_at_the_rate_this_host_charges_for_it()
    {
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var response = await owner.GetAsync(In(A, "generated-images/quality-classes"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal("Standard", body.GetProperty("defaultClass").GetString());
        Assert.Equal("Credits", body.GetProperty("unit").GetString());

        var listed = body.GetProperty("classes").EnumerateArray().ToList();
        Assert.Equal(
            ["Draft", "Standard", "High", "High2K", "High4K"],
            listed.Select(entry => entry.GetProperty("qualityClass").GetString()));

        var services = _fixture.Api.Factory.Services;
        var classes = services.GetRequiredService<IOptions<GeneratedImageQualityClassOptions>>().Value;

        await using var scope = services.CreateAsyncScope();
        var quota = scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>();

        foreach (var entry in listed)
        {
            var quality = Enum.Parse<GeneratedImageQualityClass>(entry.GetProperty("qualityClass").GetString()!);
            var sent = GeneratedImageQualityClassCatalogue.Settings(classes, quality)!;

            Assert.Equal(quota.ImageCredits(sent.ModelName, sent.Resolution, sent.Quality), entry.GetProperty("perPicture").GetDecimal());
            Assert.Equal(quality is GeneratedImageQualityClass.Standard, entry.GetProperty("isDefault").GetBoolean());
            Assert.Equal(
                ["qualityClass", "perPicture", "isDefault", "allowed", "requiresPlan", "requiresPlanName"],
                entry.EnumerateObject().Select(property => property.Name));

            // No plans configured, as this host and every deployment has it: every class may be asked for, with
            // no plan named (AP.4.3).
            Assert.True(entry.GetProperty("allowed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("requiresPlan").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("requiresPlanName").ValueKind);
        }
    }

    /// <summary>
    /// <strong>A locked class stays in the list, priced, and says what would unlock it</strong> (AP.4.3). A picker
    /// cannot weigh a plan against a figure it was not shown, and hiding the tile is presentation only.
    /// </summary>
    [Fact]
    public async Task The_classes_route_locks_what_the_accounts_plan_does_not_allow()
    {
        OnPlans();

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var body = await BodyOf(await owner.GetAsync(In(A, "generated-images/quality-classes"), Ct));

        // The default is still named, because every plan allows it — startup refuses a catalogue where one does not.
        Assert.Equal("Standard", body.GetProperty("defaultClass").GetString());

        var rows = body.GetProperty("classes").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("qualityClass").GetString()!);

        Assert.Equal(["Draft", "Standard", "High", "High2K", "High4K"], rows.Keys);

        foreach (var open in new[] { "Draft", "Standard" })
        {
            Assert.True(rows[open].GetProperty("allowed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, rows[open].GetProperty("requiresPlan").ValueKind);
        }

        foreach (var locked in new[] { "High", "High2K", "High4K" })
        {
            Assert.False(rows[locked].GetProperty("allowed").GetBoolean());
            Assert.True(rows[locked].GetProperty("perPicture").GetDecimal() > 0m);
        }

        // Each names the cheapest plan that reaches it, which is not the same plan for all three.
        Assert.Equal("creator", rows["High"].GetProperty("requiresPlan").GetString());
        Assert.Equal("Creator", rows["High"].GetProperty("requiresPlanName").GetString());
        Assert.Equal("studio", rows["High2K"].GetProperty("requiresPlan").GetString());
        Assert.Equal(JsonValueKind.Null, rows["High4K"].GetProperty("requiresPlan").ValueKind);
    }

    /// <summary>
    /// A class the plan does not allow, as the browser receives the refusal: 403, its own code, `retry: none` —
    /// a plan change is the only thing that helps — the plan named, and the field named, for the request and the
    /// quote alike. Nothing is queued.
    /// </summary>
    [Fact]
    public async Task A_class_the_plan_does_not_allow_is_refused_with_the_plan_and_the_field_named()
    {
        OnPlans();

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 1, qualityClass = "High" },
            Guid.NewGuid().ToString("N"),
            Ct);
        var quoted = await owner.GetAsync(In(A, "generated-images/quote?variants=1&qualityClass=High"), Ct);

        foreach (var refused in new[] { asked, quoted })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            var problem = await BodyOf(refused);
            Assert.Equal(MediaErrorCodes.GenerationQualityClassForbidden, problem.GetProperty("code").GetString());
            Assert.Equal("none", problem.GetProperty("retry").GetString());
            Assert.Equal("High", problem.GetProperty("qualityClass").GetString());
            Assert.Equal("creator", problem.GetProperty("requiresPlan").GetString());
            Assert.Equal("Creator", problem.GetProperty("requiresPlanName").GetString());
            // `title` is where this layer puts an application error's sentence, and where the browser reads it.
            Assert.Contains("Creator", problem.GetProperty("title").GetString()!, StringComparison.Ordinal);
            Assert.True(problem.GetProperty("errors").TryGetProperty("qualityClass", out _));

            // Nothing a creator did not ask about: no allowance, no balance, no model, no plan price.
            Assert.False(problem.TryGetProperty("remaining", out _));
            Assert.False(problem.TryGetProperty("modelName", out _));
        }

        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
    }

    /// <summary>A class the plan does allow goes through, so the gate is the plan and not the route.</summary>
    [Fact]
    public async Task A_class_the_plan_allows_is_still_requested()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        OnPlans();

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 1, qualityClass = "Draft" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Equal("Draft", (await BodyOf(asked)).GetProperty("qualityClass").GetString());
    }

    /// <summary>
    /// Three nested plans on the running host, the cheapest of them the default: the account resolves to it
    /// because it has no plan row of its own (AP.4.1).
    /// </summary>
    private void OnPlans()
    {
        var billing = _fixture.Api.Factory.Services.GetRequiredService<IOptions<BillingOptions>>().Value;

        billing.DefaultPlan = "trial";
        billing.Plans =
        [
            Plan("trial", "Trial", ["draft", "standard"]),
            Plan("creator", "Creator", ["draft", "standard", "high"]),
            Plan("studio", "Studio", ["draft", "standard", "high", "high-2k"]),
        ];
    }

    private static PlanOption Plan(string key, string name, string[] classes) => new()
    {
        Key = key,
        Name = name,
        Credits = 6000,
        DailyImageCap = 5,
        Period = PlanAllowancePeriod.Monthly,
        RunningImages = 1,
        QualityClasses = [.. classes],
    };

    /// <summary>
    /// A request at a class, through the browser's route: its quote is its hold, the operation says which class
    /// it was made at, and the price is that class's own — not the default's.
    /// </summary>
    [Fact]
    public async Task A_request_at_a_class_is_quoted_and_held_at_that_class()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var quote = await BodyOf(await owner.GetAsync(In(A, "generated-images/quote?variants=2&qualityClass=High"), Ct));
        Assert.Equal("High", quote.GetProperty("qualityClass").GetString());

        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 2, qualityClass = "High" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        var operation = await BodyOf(asked);
        Assert.Equal("High", operation.GetProperty("qualityClass").GetString());

        var held = await HeldForAsync(operation.GetProperty("id").GetGuid());
        Assert.Equal(quote.GetProperty("total").GetDecimal(), held);
        Assert.NotEqual(Credits(2), held);
    }

    /// <summary>A class that is not one of the five is not a request anybody can make.</summary>
    [Fact]
    public async Task A_quality_that_is_not_a_class_is_refused()
    {
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 1, qualityClass = "gpt-image-2-5-flare" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, asked.StatusCode);
        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
    }

    /// <summary>
    /// A class this host does not offer, as the browser receives the refusal: 422, its own code, `retry: none`
    /// — asking again unchanged cannot help — and the field named, for the request and the quote alike.
    /// </summary>
    [Fact]
    public async Task A_class_this_host_does_not_offer_is_refused_with_its_field_named()
    {
        _fixture.Api.Factory.Services.GetRequiredService<IOptions<GeneratedImageQualityClassOptions>>()
            .Value.Classes.Remove(GeneratedImageQualityClass.High4K);

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 1, qualityClass = "High4K" },
            Guid.NewGuid().ToString("N"),
            Ct);
        var quoted = await owner.GetAsync(In(A, "generated-images/quote?variants=1&qualityClass=High4K"), Ct);

        foreach (var refused in new[] { asked, quoted })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

            var problem = await BodyOf(refused);
            Assert.Equal(MediaErrorCodes.GenerationQualityClassUnprocessable, problem.GetProperty("code").GetString());
            Assert.Equal("none", problem.GetProperty("retry").GetString());
            Assert.True(problem.GetProperty("errors").TryGetProperty("qualityClass", out _));
        }

        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
    }

    /// <summary>A Viewer cannot ask for pictures, so the classes route answers 403, as the quote does.</summary>
    [Fact]
    public async Task A_viewer_is_refused_the_classes()
    {
        using var viewer = await _fixture.SignInAsync(B.MemberEmail, cancellationToken: Ct);

        var response = await viewer.GetAsync(In(B, "generated-images/quality-classes"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<decimal> HeldForAsync(Guid operationId)
    {
        // The hold is platform-scoped and keyed by the operation it was admitted for, so it is read without a
        // workspace — the operation row itself is workspace-owned and would need one.
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations
            .Where(row => row.AiOperationId == operationId)
            .Select(row => row.ReservedAmount)
            .SingleAsync(Ct);
    }
}
