using Microsoft.Extensions.AI;

namespace CreatorPantry.Tests.Ai.Live;

/// <summary>
/// What each live call used and how it ended, written to the file <c>CP_LIVE_USAGE</c> names when it is set
/// (AP.2.2). The finish reason says whether the output ceiling cut an answer off; the counts are what a
/// call settles at.
/// </summary>
/// <remarks>
/// Counts and identifiers only — never a prompt or an answer, which the transcript (<c>CP_LIVE_OUT</c>) already
/// holds when asked for. Nothing is written when the variable is unset.
/// </remarks>
internal static class LiveUsage
{
    private const string UsageVariable = "CP_LIVE_USAGE";
    private static readonly Lock Gate = new();

    public static void Record(string check, ChatOptions? options, ChatResponse response)
    {
        if (Environment.GetEnvironmentVariable(UsageVariable) is not { Length: > 0 } path)
        {
            return;
        }

        var usage = response.Usage;
        var extra = usage?.AdditionalCounts is { Count: > 0 } counts
            ? " " + string.Join(" ", counts.Select(pair => $"{pair.Key}={pair.Value}"))
            : string.Empty;

        var line = $"{check} finish={response.FinishReason?.Value ?? "none"} model={response.ModelId} "
            + $"ceiling={options?.MaxOutputTokens?.ToString() ?? "none"} in={usage?.InputTokenCount} "
            + $"out={usage?.OutputTokenCount}{extra}{Environment.NewLine}";

        lock (Gate)
        {
            File.AppendAllText(path, line);
        }
    }
}
