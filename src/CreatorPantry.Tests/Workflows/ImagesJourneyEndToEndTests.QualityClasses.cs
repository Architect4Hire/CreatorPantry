using System.Net;
using System.Net.Http.Json;
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
            Assert.Equal(["qualityClass", "perPicture", "isDefault"], entry.EnumerateObject().Select(property => property.Name));
        }
    }

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
