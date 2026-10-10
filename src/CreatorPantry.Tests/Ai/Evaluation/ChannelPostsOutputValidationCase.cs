using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through <see cref="AiChannelPostsOutputValidator.Validate"/> and, when the answer validates,
/// through the same measuring and claim scanning <see cref="ChannelPostsAiTaskHandler"/> applies before it
/// stores a proposal — no envelope, no gateway, no database.
/// </summary>
/// <remarks>
/// <para>
/// A rejected fixture asserts the reason code. An accepted one may also assert, per channel, the limit verdict
/// the server recorded, a finding it must have raised, and words it must <em>not</em> have flagged — and every
/// accepted fixture asserts, without being asked, that each body was stored exactly as the model wrote it.
/// </para>
/// <para>
/// The recipe a claim is checked against is the fixture's own, built into the same package shape the handler
/// reads. It proves what the scanners do with a given answer. It does not and cannot prove what a model writes.
/// </para>
/// </remarks>
internal sealed class ChannelPostsOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly ContentChannelProfileCatalog Profiles = new();

    public AiEvaluationKind Kind => AiEvaluationKind.ChannelPostsOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        if (input.RequestedChannels is not { Count: > 0 })
        {
            throw new AiEvaluationException(fixture.Identity, "'input.requestedChannels' names no channel.");
        }

        var result = AiChannelPostsOutputValidator.Validate(
            input.Payload, input.ExpectedSchemaVersion, input.RequestedChannels);

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => result.Succeeded
                ? Accepted(fixture.Identity, result.Document!, input, expect)
                : AiEvaluationVerdict.Fail(
                    $"expected the answer to validate, but it was rejected: {result.Failure!.ReasonCode} "
                    + $"({result.Failure.Message})"),

            "Rejected" => Rejected(fixture.Identity, result, expect),

            _ => throw new AiEvaluationException(
                fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    private static AiEvaluationVerdict Accepted(
        string fixtureIdentity, AiChannelPostsOutputDocument document, Input input, Expect expect)
    {
        var requested = input.RequestedChannels!
            .Select(key => Profiles.Find(key)
                ?? throw new AiEvaluationException(fixtureIdentity, $"'{key}' has no channel writing profile."))
            .ToList();

        var (changes, warnings) = ChannelPostsAiTaskHandler.Translate(
            document, requested, Package(input), Profiles.Version);

        // Never trimmed, whatever the fixture is about: an over-limit body is stored as written.
        foreach (var post in document.Posts)
        {
            var stored = changes.Single(change =>
                change.ChangeKind is AiChangeKind.Add && change.TargetId == Target(changes, post.ChannelKey));

            if (!string.Equals(stored.AfterValue, post.Body, StringComparison.Ordinal))
            {
                return AiEvaluationVerdict.Fail($"the body for '{post.ChannelKey}' was not stored as written.");
            }
        }

        foreach (var (channelKey, status) in expect.Limits ?? new Dictionary<string, string>())
        {
            var recorded = changes.Single(change =>
                change.TargetId == Target(changes, channelKey) && change.FieldName == ChannelPostFields.LimitStatus);

            if (recorded.AfterValue != status)
            {
                return AiEvaluationVerdict.Fail(
                    $"expected '{channelKey}' to be recorded '{status}', got '{recorded.AfterValue}'.");
            }
        }

        foreach (var flag in expect.Flagged ?? [])
        {
            var bodyRow = changes.Single(change =>
                change.ChangeKind is AiChangeKind.Add && change.TargetId == Target(changes, flag.ChannelKey)).SortOrder;

            if (!warnings.Any(warning =>
                    warning.ChangeIndex == bodyRow
                    && warning.Message.Contains(flag.Containing, StringComparison.OrdinalIgnoreCase)))
            {
                return AiEvaluationVerdict.Fail(
                    $"expected a finding on '{flag.ChannelKey}' containing \"{flag.Containing}\", but found: "
                    + (warnings.Count == 0 ? "none" : string.Join(" | ", warnings.Select(warning => warning.Message))));
            }
        }

        foreach (var phrase in expect.NotFlagged ?? [])
        {
            if (warnings.FirstOrDefault(warning =>
                    warning.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase)) is { } unexpected)
            {
                return AiEvaluationVerdict.Fail(
                    $"expected nothing to be flagged about \"{phrase}\", but found: {unexpected.Message}");
            }
        }

        if (expect.NoFindings is true && warnings.Count > 0)
        {
            return AiEvaluationVerdict.Fail(
                "expected no findings, but found: " + string.Join(" | ", warnings.Select(warning => warning.Message)));
        }

        return AiEvaluationVerdict.Pass();
    }

    private static Guid? Target(IReadOnlyList<AiResolvedChange> changes, string channelKey) =>
        changes.Single(change =>
            change.FieldName == ChannelPostFields.ChannelKey && change.AfterValue == channelKey).TargetId;

    /// <summary>The fixture's material as the package the handler would have read it from.</summary>
    private static CreativeContextPackage Package(Input input)
    {
        var recipes = input.Recipe is { } recipe
            ? new List<CreativeContextRecipeEntry>
            {
                new(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    1,
                    recipe.Title,
                    recipe.YieldText,
                    recipe.PrepTimeMinutes,
                    recipe.CookTimeMinutes,
                    RestTimeMinutes: null,
                    TotalTimeMinutes: null,
                    recipe.Ingredients ?? [],
                    recipe.Steps ?? [],
                    0,
                    0),
            }
            : [];

        return new CreativeContextPackage(
            Guid.NewGuid(),
            AiTaskType.ChannelPosts,
            Guid.NewGuid(),
            ContextVersion: null,
            new CreativeContextWords(input.WorkingTitle, PictureBrief: null),
            Channels: [],
            Day: null,
            recipes,
            Concepts: [],
            Pictures: [],
            Prompts: [],
            Dropped: [],
            Omissions: [],
            EstimatedTokens: 0,
            Checksum: "sha256:fixture",
            AssembledAt: DateTimeOffset.UnixEpoch);
    }

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity, AiOutputValidationOutcome<AiChannelPostsOutputDocument> result, Expect expect)
    {
        if (expect.ReasonCode is null)
        {
            throw new AiEvaluationException(
                fixtureIdentity, "'expect.reasonCode' is required when 'expect.outcome' is 'Rejected'.");
        }

        if (result.Succeeded)
        {
            return AiEvaluationVerdict.Fail("expected the answer to be rejected, but it validated.");
        }

        if (result.Failure!.ReasonCode != expect.ReasonCode)
        {
            return AiEvaluationVerdict.Fail(
                $"expected reason '{expect.ReasonCode}', got '{result.Failure.ReasonCode}' "
                + $"({result.Failure.Message}).");
        }

        if (expect.Category is { } category && result.Failure.Category != category)
        {
            return AiEvaluationVerdict.Fail(
                $"expected category '{category}', got '{result.Failure.Category}'.");
        }

        return AiEvaluationVerdict.Pass();
    }

    private sealed record Input(
        string? Payload,
        string ExpectedSchemaVersion,
        IReadOnlyList<string>? RequestedChannels,
        string? WorkingTitle = null,
        RecipeInput? Recipe = null);

    private sealed record RecipeInput(
        string Title,
        string? YieldText = null,
        int? PrepTimeMinutes = null,
        int? CookTimeMinutes = null,
        IReadOnlyList<string>? Ingredients = null,
        IReadOnlyList<string>? Steps = null);

    private sealed record Flag(string ChannelKey, string Containing);

    private sealed record Expect(
        string Outcome,
        string? ReasonCode = null,
        AiFailureCategory? Category = null,
        IReadOnlyDictionary<string, string>? Limits = null,
        IReadOnlyList<Flag>? Flagged = null,
        IReadOnlyList<string>? NotFlagged = null,
        bool? NoFindings = null);
}
