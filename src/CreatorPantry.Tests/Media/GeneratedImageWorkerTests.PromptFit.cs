using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The prompt fit (AP.3.2): what would be sent is measured against the model the chosen class sends to before
/// anything is held or queued, and a prompt over it is refused — never cut — with the figures a client acts on.
/// </summary>
/// <remarks>
/// Draft's fake model is given a short limit here and the others none, so a refusal that came from the wrong
/// class's model, or a measure taken when nothing said a limit, cannot pass for the right one.
/// </remarks>
public sealed partial class GeneratedImageWorkerTests
{
    private const int DraftLimit = 120;

    private static readonly string Avoid = "harsh flash; plastic props";

    /// <summary>
    /// <strong>Over the class's limit with room for a shorter prompt</strong>: refused naming the prompt, carrying
    /// the limit, the length and the budget left once the avoid list is set aside — and nothing held, queued or sent.
    /// </summary>
    [Fact]
    public async Task A_prompt_over_the_class_limit_is_refused_with_its_figures_and_nothing_is_held()
    {
        _generator.PromptLimits[ImageQuotaTestSettings.DraftModel] = DraftLimit;
        var prompt = new string('p', 110);

        var result = await RequestDraftAsync(prompt, Avoid);

        var error = result.Error!;
        Assert.Equal(MediaErrorCodes.GenerationPromptUnprocessable, error.Code);
        Assert.Contains("promptText", error.FieldErrors.Keys);

        var sent = GeneratedImagePromptComposition.Compose(prompt, Avoid).Length;
        Assert.Equal(DraftLimit, error.Extensions!["characterLimit"]);
        Assert.Equal(sent, error.Extensions["characterCount"]);
        Assert.Equal(DraftLimit - (sent - prompt.Length), error.Extensions["promptBudget"]);

        Assert.Empty(await OperationsAsync(WorkspaceA));
        await RunAsync();
        Assert.Equal(0, _generator.Calls);
    }

    /// <summary>
    /// <strong>The avoid list alone leaves no room</strong>: refused naming the avoid list instead, because no
    /// shorter prompt can fix it and the avoid list is never shortened for the creator.
    /// </summary>
    [Fact]
    public async Task An_avoid_list_that_leaves_no_room_is_refused_for_itself()
    {
        _generator.PromptLimits[ImageQuotaTestSettings.DraftModel] = DraftLimit;
        var avoid = new string('a', DraftLimit - MediaPolicy.ShortestPromptBudget);

        var result = await RequestDraftAsync("a bowl of soup on a wooden table", avoid);

        Assert.Equal(MediaErrorCodes.GenerationAvoidUnprocessable, result.Error!.Code);
        Assert.Contains("avoidText", result.Error.FieldErrors.Keys);
        Assert.True((int)result.Error.Extensions!["promptBudget"]! < MediaPolicy.ShortestPromptBudget);
        Assert.Empty(await OperationsAsync(WorkspaceA));
    }

    /// <summary>
    /// <strong>Exactly at the limit is within it</strong>, and what reaches the model is that string, uncut: the
    /// measure and the send are one composition.
    /// </summary>
    [Fact]
    public async Task A_prompt_exactly_at_the_limit_is_sent_whole()
    {
        _generator.PromptLimits[ImageQuotaTestSettings.DraftModel] = DraftLimit;
        var room = DraftLimit - (GeneratedImagePromptComposition.Compose(string.Empty, Avoid).Length);
        var prompt = new string('p', room);

        var result = await RequestDraftAsync(prompt, Avoid);
        Assert.True(result.Succeeded, result.Error?.Message);

        await RunAsync();

        var sent = Assert.Single(_generator.Prompts);
        Assert.Equal(DraftLimit, sent.Length);
        Assert.Equal(GeneratedImagePromptComposition.Compose(prompt, Avoid), sent);
    }

    /// <summary>
    /// The limit is the chosen class's model's, not any other: the same long prompt is refused at Draft and goes
    /// through at Standard, whose model says no limit.
    /// </summary>
    [Fact]
    public async Task The_limit_is_the_chosen_class_models_own()
    {
        _generator.PromptLimits[ImageQuotaTestSettings.DraftModel] = DraftLimit;
        var prompt = new string('p', 200);

        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();
        var ct = TestContext.Current.CancellationToken;

        var atDraft = await facade.RequestAsync(
            Request(prompt: prompt) with { QualityClass = GeneratedImageQualityClass.Draft }, ct);
        var atStandard = await facade.RequestAsync(
            Request(prompt: prompt) with { QualityClass = GeneratedImageQualityClass.Standard }, ct);

        Assert.Equal(MediaErrorCodes.GenerationPromptUnprocessable, atDraft.Error!.Code);
        Assert.True(atStandard.Succeeded, atStandard.Error?.Message);
    }

    /// <summary>
    /// <strong>A replay is never refused</strong>: a key that bought pictures is answered with them even if the
    /// limit has since fallen below what it sent.
    /// </summary>
    [Fact]
    public async Task A_replayed_key_is_answered_after_the_limit_falls()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();
        var ct = TestContext.Current.CancellationToken;
        var request = Request(prompt: new string('p', 200), key: "bought-before-the-limit") with
        {
            QualityClass = GeneratedImageQualityClass.Draft,
        };

        var first = await facade.RequestAsync(request, ct);

        _generator.PromptLimits[ImageQuotaTestSettings.DraftModel] = DraftLimit;

        var replayed = await facade.RequestAsync(request, ct);

        Assert.True(replayed.Succeeded, replayed.Error?.Message);
        Assert.Equal(first.Value!.Id, replayed.Value!.Id);
    }

    /// <summary>
    /// The measure the Ai module asks for a shorter prompt: the request's own figures, trimmed as the request trims
    /// them; nothing to measure against when the model says no limit; a Viewer and an unoffered class refused as
    /// the request refuses them.
    /// </summary>
    [Fact]
    public async Task The_measure_is_the_requests_own_and_refuses_what_the_request_refuses()
    {
        _generator.PromptLimits[ImageQuotaTestSettings.DraftModel] = DraftLimit;
        var prompt = new string('p', 110);

        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();

        var draft = facade.MeasurePrompt($"  {prompt}  ", $" {Avoid} ", GeneratedImageQualityClass.Draft);
        var standard = facade.MeasurePrompt(prompt, Avoid, GeneratedImageQualityClass.Standard);
        var unoffered = facade.MeasurePrompt(prompt, Avoid, GeneratedImageQualityClass.High4K);

        Assert.Equal(GeneratedImagePromptComposition.Measure(prompt, Avoid, DraftLimit), draft.Value);
        Assert.True(standard.Succeeded);
        Assert.Null(standard.Value);
        Assert.Equal(MediaErrorCodes.GenerationQualityClassUnprocessable, unoffered.Error!.Code);

        await using var viewer = ScopeFor(WorkspaceA, WorkspaceRole.Viewer);
        var refused = viewer.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .MeasurePrompt(prompt, Avoid, GeneratedImageQualityClass.Draft);

        Assert.Equal(MediaErrorCodes.GenerationForbidden, refused.Error!.Code);
    }

    private async Task<Domain.Managers.Results.OperationResult<GeneratedImageOperationServiceModel>> RequestDraftAsync(
        string prompt, string? avoid)
    {
        await using var scope = ScopeFor(WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>().RequestAsync(
            Request(prompt: prompt) with { AvoidText = avoid, QualityClass = GeneratedImageQualityClass.Draft },
            TestContext.Current.CancellationToken);
    }
}
