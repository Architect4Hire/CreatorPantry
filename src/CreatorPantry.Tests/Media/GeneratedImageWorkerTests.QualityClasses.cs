using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Tests.Brand;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// Quality classes (AP.3.1, PRICE-006, B-55): a request names a class, and the hold, the quote, what is sent and
/// what is charged are all that class's — its own model, settings and rate.
/// </summary>
/// <remarks>
/// The fixture offers three classes on fake models at distinct rates (<see cref="ImageQuotaTestSettings"/>), so a
/// figure that came from the wrong class cannot pass for the right one.
/// </remarks>
public sealed partial class GeneratedImageWorkerTests
{
    public static TheoryData<GeneratedImageQualityClass, string, string?, string?, decimal> OfferedClasses => new()
    {
        { GeneratedImageQualityClass.Draft, ImageQuotaTestSettings.DraftModel, null, null, ImageQuotaTestSettings.DraftRate },
        { GeneratedImageQualityClass.Standard, TestDatabase.FakeImageModel, null, null, ImageQuotaTestSettings.RatePerImage },
        { GeneratedImageQualityClass.High, ImageQuotaTestSettings.HighModel, "high", "1K", ImageQuotaTestSettings.HighRate },
    };

    /// <summary>
    /// <strong>One class, one rate, everywhere.</strong> Two pictures at a class: the quote says that class's
    /// price, the hold is exactly the quote, the worker sends that class's model and settings, provenance names
    /// that model, and the settlement charges that class's rate for what was made.
    /// </summary>
    [Theory]
    [MemberData(nameof(OfferedClasses))]
    public async Task A_class_is_quoted_held_sent_and_charged_at_its_own_rate(
        GeneratedImageQualityClass quality, string model, string? qualitySent, string? resolution, decimal rate)
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        var quote = await QuoteAsync(variants: 2, quality);
        Assert.Equal(quality, quote.QualityClass);
        Assert.Equal(rate, quote.PerPicture);
        Assert.Equal(rate * 2, quote.Total);

        var requested = await RequestAtAsync(variants: 2, quality);
        Assert.Equal(quality, requested.QualityClass);
        Assert.Equal(quality, (await OperationAsync(WorkspaceA, requested.Id)).QualityClass);

        // The quote is the hold: one price, read once, by the code that admits.
        Assert.Equal(quote.Total, (await ReservationAsync(requested.Id)).ReservedAmount);

        await RunAsync();

        Assert.Equal(2, _generator.RequestedOptions.Count);
        Assert.All(_generator.RequestedOptions, options =>
        {
            Assert.Equal(model, options!.ModelId);
            Assert.Equal(qualitySent, ImageGenerationSettings.Read(options.AdditionalProperties, ImageGenerationSettings.Quality));
            Assert.Equal(resolution, ImageGenerationSettings.Read(options.AdditionalProperties, ImageGenerationSettings.Resolution));
        });

        // Provenance names the model the class sent to, not the deployment's default model.
        Assert.Equal(model, (await OperationAsync(WorkspaceA, requested.Id)).ModelName);
        Assert.Equal(rate * 2, (await ReservationAsync(requested.Id)).SettledAmount);
    }

    /// <summary>A request that names no class is made at the default — and says so — exactly as before classes.</summary>
    [Fact]
    public async Task A_request_that_names_no_class_is_made_at_the_default()
    {
        var requested = await RequestAsync(WorkspaceA, variants: 1);

        Assert.Equal(GeneratedImageQualityClass.Standard, requested.QualityClass);
        Assert.Equal(ImageQuotaTestSettings.RatePerImage, (await ReservationAsync(requested.Id)).ReservedAmount);

        await RunAsync();

        Assert.Equal(TestDatabase.FakeImageModel, Assert.Single(_generator.RequestedOptions)!.ModelId);
    }

    /// <summary>
    /// A class this host does not offer is refused in words, naming the field — for the price and for the
    /// request alike — and nothing is held or queued.
    /// </summary>
    [Fact]
    public async Task A_class_this_host_does_not_offer_is_refused_and_holds_nothing()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();

        var quote = await facade.QuoteAsync(1, GeneratedImageQualityClass.High4K, TestContext.Current.CancellationToken);
        var request = await facade.RequestAsync(
            Request() with { QualityClass = GeneratedImageQualityClass.High4K }, TestContext.Current.CancellationToken);

        Assert.Equal(MediaErrorCodes.GenerationQualityClassUnprocessable, quote.Error!.Code);
        Assert.Equal(MediaErrorCodes.GenerationQualityClassUnprocessable, request.Error!.Code);
        Assert.Contains("qualityClass", request.Error.FieldErrors.Keys);
        Assert.Empty(await OperationsAsync(WorkspaceA));
    }

    /// <summary>
    /// <strong>A key names one request, quality included</strong> (12.10l): the same key at another class is a
    /// different request and is refused; the same key naming the default is the request that named none.
    /// </summary>
    [Fact]
    public async Task A_key_reused_at_another_class_is_refused_and_one_naming_the_default_is_the_same_request()
    {
        // One member throughout: a key reused by somebody else is refused for that reason, not this one.
        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();
        var ct = TestContext.Current.CancellationToken;

        var first = await facade.RequestAsync(Request(key: "one-request"), ct);
        var sameRequest = await facade.RequestAsync(
            Request(key: "one-request") with { QualityClass = GeneratedImageQualityClass.Standard }, ct);
        var reused = await facade.RequestAsync(
            Request(key: "one-request") with { QualityClass = GeneratedImageQualityClass.High }, ct);

        Assert.Equal(first.Value!.Id, sameRequest.Value!.Id);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, reused.Error!.Code);
        Assert.Single(await OperationsAsync(WorkspaceA));
    }

    /// <summary>
    /// <strong>A replay is never refused</strong>, even when the class it was bought at has since been taken off
    /// this host: its answer is the operation it already bought.
    /// </summary>
    [Fact]
    public async Task A_replayed_key_is_answered_even_after_its_class_is_taken_off()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();
        var ct = TestContext.Current.CancellationToken;
        var request = Request(key: "bought-at-high") with { QualityClass = GeneratedImageQualityClass.High };

        var first = await facade.RequestAsync(request, ct);

        _provider.GetRequiredService<IOptions<GeneratedImageQualityClassOptions>>()
            .Value.Classes.Remove(GeneratedImageQualityClass.High);

        var replayed = await facade.RequestAsync(request, ct);

        Assert.True(replayed.Succeeded, replayed.Error?.Message);
        Assert.Equal(first.Value!.Id, replayed.Value!.Id);
    }

    /// <summary>
    /// A lost answer retried with the body it was sent with — naming no class — finds its operation even if the
    /// default changed in between, rather than being told its key belongs to a different request.
    /// </summary>
    [Fact]
    public async Task A_retry_naming_no_class_finds_its_operation_after_the_default_changes()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();
        var ct = TestContext.Current.CancellationToken;

        var first = await facade.RequestAsync(Request(key: "lost-answer"), ct);

        _provider.GetRequiredService<IOptions<GeneratedImageQualityClassOptions>>()
            .Value.DefaultClass = GeneratedImageQualityClass.Draft;

        var retried = await facade.RequestAsync(Request(key: "lost-answer"), ct);

        Assert.Equal(first.Value!.Id, retried.Value!.Id);
        Assert.Equal(GeneratedImageQualityClass.Standard, retried.Value.QualityClass);
    }

    /// <summary>
    /// A class taken off this host between the request and the run is not sent at some other setting: the hold
    /// priced that class, and a picture of anything else is not what was asked for or paid for.
    /// </summary>
    [Fact]
    public async Task A_class_no_longer_offered_when_the_worker_runs_fails_rather_than_sending_something_else()
    {
        var requested = await RequestAtAsync(variants: 1, GeneratedImageQualityClass.High);

        _provider.GetRequiredService<IOptions<GeneratedImageQualityClassOptions>>()
            .Value.Classes.Remove(GeneratedImageQualityClass.High);

        await RunAsync();

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.Failed, operation.Status);
        Assert.Equal(GeneratedImageFailureCategory.ProviderNotConfigured, operation.FailureCategory);
        Assert.Equal(0, _generator.Calls);
    }

    /// <summary>
    /// The price list: every offered class, in ascending order, at exactly the rate a hold for one picture of it
    /// takes (B-55), the default marked — and nothing a creator could send instead of a class.
    /// </summary>
    [Fact]
    public async Task The_classes_list_prices_each_offered_class_at_its_rate_in_order()
    {
        await using var scope = ScopeFor(WorkspaceA);

        var result = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .ListQualityClassesAsync(TestContext.Current.CancellationToken);

        var list = result.Value!;
        Assert.Equal(GeneratedImageQualityClass.Standard, list.DefaultClass);
        Assert.Equal(AiQuotaUnit.Credits.ToString(), list.Unit);
        Assert.Equal(
            [
                (GeneratedImageQualityClass.Draft, ImageQuotaTestSettings.DraftRate, false),
                (GeneratedImageQualityClass.Standard, ImageQuotaTestSettings.RatePerImage, true),
                (GeneratedImageQualityClass.High, ImageQuotaTestSettings.HighRate, false),
            ],
            list.Classes.Select(entry => (entry.QualityClass, entry.PerPicture, entry.IsDefault)));

        // And each is what a quote for one picture of that class says.
        foreach (var entry in list.Classes)
        {
            Assert.Equal(entry.PerPicture, (await QuoteAsync(variants: 1, entry.QualityClass)).PerPicture);
        }
    }

    /// <summary>A Viewer cannot ask for pictures, so has no classes to choose between either.</summary>
    [Fact]
    public async Task A_viewer_cannot_read_the_classes()
    {
        await using var scope = ScopeFor(WorkspaceA, WorkspaceRole.Viewer);

        var result = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .ListQualityClassesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MediaErrorCodes.GenerationForbidden, result.Error!.Code);
    }

    private async Task<GeneratedImageQuoteServiceModel> QuoteAsync(int variants, GeneratedImageQualityClass? quality)
    {
        await using var scope = ScopeFor(WorkspaceA);

        var result = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .QuoteAsync(variants, quality, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error?.Message);

        return result.Value!;
    }

    private async Task<GeneratedImageOperationServiceModel> RequestAtAsync(
        int variants, GeneratedImageQualityClass quality, string? key = null)
    {
        await using var scope = ScopeFor(WorkspaceA);

        var result = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>().RequestAsync(
            Request(variants, key: key) with { QualityClass = quality }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error?.Message);

        return result.Value!;
    }
}
