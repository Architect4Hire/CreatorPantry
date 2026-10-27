using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// The prompt fit and the shorter prompt (AP.3.2) as the browser meets them: a picture request over the chosen
/// class's limit refused with its figures, a shorter prompt asked for to exactly that budget, and the refusals of a
/// request that does not need one — across two workspaces.
/// </summary>
/// <remarks>
/// Draft's model is given a short limit through the fake generator, the way a real short-limit model's profile
/// would give one. No worker runs the shorter prompt here; the handler tests do that against a fake model.
/// </remarks>
public sealed partial class ImagesJourneyEndToEndTests
{
    private const int DraftLimit = 200;

    private const string LongPrompt =
        "Overhead shot of a plum galette on a linen cloth, soft window light from the left, a dusting of icing "
        + "sugar on the crust, a small jug of cream just in frame, two forks on a folded napkin, crumbs scattered "
        + "towards the corner, warm and unhurried.";

    private const string AvoidList = "harsh flash; plastic props";

    /// <summary>
    /// <strong>Refused before anything is held, with what to do next</strong>: 422, the prompt named, `retry: none`,
    /// and the limit, the length and the budget — the budget being exactly what the shorter-prompt route is then
    /// held to.
    /// </summary>
    [Fact]
    public async Task A_picture_request_over_the_class_limit_is_refused_with_its_figures_and_queues_nothing()
    {
        LimitDraft();
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = LongPrompt, avoidText = AvoidList, variantCount = 1, qualityClass = "Draft" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, asked.StatusCode);

        var problem = await BodyOf(asked);
        var sent = GeneratedImagePromptComposition.Compose(LongPrompt, AvoidList).Length;
        Assert.Equal(MediaErrorCodes.GenerationPromptUnprocessable, problem.GetProperty("code").GetString());
        Assert.Equal("none", problem.GetProperty("retry").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("promptText", out _));
        Assert.Equal(DraftLimit, problem.GetProperty("characterLimit").GetInt32());
        Assert.Equal(sent, problem.GetProperty("characterCount").GetInt32());
        Assert.Equal(DraftLimit - (sent - LongPrompt.Length), problem.GetProperty("promptBudget").GetInt32());

        // Nothing of the creator's text comes back in the refusal.
        Assert.DoesNotContain("galette", await asked.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
    }

    /// <summary>
    /// <strong>The shorter prompt is an image-prompt request</strong>: 202, read back from the same status
    /// resource a composed prompt is — and not from a neighbouring workspace, which is told it does not exist.
    /// </summary>
    [Fact]
    public async Task A_shorter_prompt_is_queued_as_an_image_prompt_request_the_neighbour_cannot_read()
    {
        LimitDraft();
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var neighbour = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);

        var asked = await owner.PostAsJsonAsync(
            In(A, "image-prompt-requests/compactions"),
            new { promptText = LongPrompt, avoidText = AvoidList, qualityClass = "Draft" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var requestId = (await BodyOf(asked)).GetProperty("aiProposalRequestId").GetGuid();
        Assert.Equal($"/api/v1/workspaces/{A.Slug}/image-prompt-requests/{requestId}", asked.Headers.Location!.OriginalString);

        var mine = await owner.GetAsync(In(A, $"image-prompt-requests/{requestId}"), Ct);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);

        var theirs = await neighbour.GetAsync(In(B, $"image-prompt-requests/{requestId}"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(
            AiImagePromptRequestErrors.RequestNotFound,
            (await BodyOf(theirs)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A prompt that fits is not shortened — the creator's words changed and their allowance spent for nothing —
    /// and one whose avoid list alone leaves no room cannot be: each refused for its own field, with the figures.
    /// </summary>
    [Fact]
    public async Task A_shorter_prompt_is_refused_when_none_is_needed_or_none_could_fit()
    {
        LimitDraft();
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var fits = await owner.PostAsJsonAsync(
            In(A, "image-prompt-requests/compactions"),
            new { promptText = "A plum galette on linen, soft window light.", qualityClass = "Draft" },
            Guid.NewGuid().ToString("N"),
            Ct);
        var crowded = await owner.PostAsJsonAsync(
            In(A, "image-prompt-requests/compactions"),
            new { promptText = LongPrompt, avoidText = new string('a', DraftLimit), qualityClass = "Draft" },
            Guid.NewGuid().ToString("N"),
            Ct);

        foreach (var (refused, code, field) in new[]
        {
            (fits, AiImagePromptCompactionErrors.AlreadyFits, "promptText"),
            (crowded, AiImagePromptCompactionErrors.AvoidTooLong, "avoidText"),
        })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

            var problem = await BodyOf(refused);
            Assert.Equal(code, problem.GetProperty("code").GetString());
            Assert.Equal("none", problem.GetProperty("retry").GetString());
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
            Assert.Equal(DraftLimit, problem.GetProperty("characterLimit").GetInt32());
        }
    }

    /// <summary>A Viewer cannot ask for pictures, so cannot ask for a shorter prompt for them either.</summary>
    [Fact]
    public async Task A_viewer_cannot_ask_for_a_shorter_prompt()
    {
        LimitDraft();
        using var viewer = await _fixture.SignInAsync(B.MemberEmail, cancellationToken: Ct);

        var asked = await viewer.PostAsJsonAsync(
            In(B, "image-prompt-requests/compactions"),
            new { promptText = LongPrompt, qualityClass = "Draft" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, asked.StatusCode);
    }

    /// <summary>
    /// <strong>A replayed key is the request it replays</strong>: the same operation, said to be a replay, and not
    /// measured again — so a limit raised in between, which would now refuse a fresh request as already fitting,
    /// does not refuse it, and nothing is queued twice.
    /// </summary>
    [Fact]
    public async Task A_replayed_shorter_prompt_request_is_answered_with_its_operation_and_not_measured_again()
    {
        LimitDraft();
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var body = new { promptText = LongPrompt, avoidText = AvoidList, qualityClass = "Draft" };
        const string key = "shorter-prompt-replay";

        var first = await owner.PostAsJsonAsync(In(A, "image-prompt-requests/compactions"), body, key, Ct);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var requestId = (await BodyOf(first)).GetProperty("aiProposalRequestId").GetGuid();

        // Now the prompt would fit, and a new request for it would be refused.
        LimitDraft(10_000);

        var replayed = await owner.PostAsJsonAsync(In(A, "image-prompt-requests/compactions"), body, key, Ct);
        var fresh = await owner.PostAsJsonAsync(
            In(A, "image-prompt-requests/compactions"), body, Guid.NewGuid().ToString("N"), Ct);

        Assert.Equal(HttpStatusCode.Accepted, replayed.StatusCode);
        Assert.Equal(requestId, (await BodyOf(replayed)).GetProperty("aiProposalRequestId").GetGuid());
        Assert.Equal("true", Assert.Single(replayed.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fresh.StatusCode);
    }

    /// <summary>Gives Draft's model, whatever this host maps it to, a prompt limit — short unless told otherwise.</summary>
    private void LimitDraft(int limit = DraftLimit)
    {
        var classes = _fixture.Api.Factory.Services
            .GetRequiredService<IOptions<GeneratedImageQualityClassOptions>>().Value;

        _generator.PromptLimits[GeneratedImageQualityClassCatalogue.Settings(classes, GeneratedImageQualityClass.Draft)!.ModelName] =
            limit;
    }
}
