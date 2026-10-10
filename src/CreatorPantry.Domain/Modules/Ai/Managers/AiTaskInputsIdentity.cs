using System.Text.Json;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The part of an operation's stored inputs that <em>is</em> the request, for comparing a replayed idempotency key
/// with the operation it names.
/// </summary>
/// <remarks>
/// <para>
/// For most tasks every input is the creator's question, so the whole string is compared. The two content packages
/// are different: they also store facts about the world at the moment of the request — the brand profile's
/// name, audience and locale, and the rules version — so the operation is reproducible. Those are not part of what
/// was asked. Comparing them would refuse a genuine retry as "a different request" the moment the creator edited
/// their brand profile in between, breaking the promise that a retry returns the first answer.
/// </para>
/// <para>
/// A replay therefore returns the original operation, pins and all: it describes what actually ran, which is what
/// the creator's first request bought.
/// </para>
/// </remarks>
internal static class AiTaskInputsIdentity
{
    public static string? Of(AiTaskType task, string? inputsJson)
    {
        if (inputsJson is not null && task is AiTaskType.RecipeFirstDraft)
        {
            return WithoutWorkspaceFacts(inputsJson);
        }

        if (inputsJson is null || task is not (AiTaskType.EditorialPackage or AiTaskType.SeoPackage))
        {
            return inputsJson;
        }

        try
        {
            var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(inputsJson);

            // Both packages name their requested sections under the same key.
            return inputs is not null && inputs.TryGetValue(AiEditorialPackageInputs.Sections, out var sections)
                ? sections
                : inputsJson;
        }
        catch (JsonException)
        {
            return inputsJson;
        }
    }

    /// <summary>
    /// A first draft's inputs minus the workspace's measurement system: the same reasoning as the packages'
    /// brand facts. An Owner changing the setting between a request and its retry has not asked a different
    /// question, and the retry should return the draft the first request bought.
    /// </summary>
    private static string WithoutWorkspaceFacts(string inputsJson)
    {
        try
        {
            var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(inputsJson);

            if (inputs is null)
            {
                return inputsJson;
            }

            foreach (var key in AiFirstDraftInputs.WorkspaceFacts)
            {
                inputs.Remove(key);
            }

            return JsonSerializer.Serialize(
                inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return inputsJson;
        }
    }
}
