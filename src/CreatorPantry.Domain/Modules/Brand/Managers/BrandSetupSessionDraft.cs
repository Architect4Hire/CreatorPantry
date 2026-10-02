using System.Text;
using System.Text.Json;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The one definition of an acceptable setup-session draft, shared by the facade's validator and Business so
/// the two cannot drift: a JSON object of at most 64 KB of UTF-8.
/// </summary>
public static class BrandSetupSessionDraft
{
    /// <summary>The largest draft accepted, in UTF-8 bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>A message describing why the draft is unacceptable, or null when it is fine.</summary>
    public static string? Problem(string? draft)
    {
        if (draft is null)
        {
            return "draftJson is required; send \"{}\" for an empty draft.";
        }

        if (draft.Length > MaxBytes || Encoding.UTF8.GetByteCount(draft) > MaxBytes)
        {
            return "draftJson is larger than 64 KB.";
        }

        try
        {
            using var document = JsonDocument.Parse(draft);

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? null
                : "draftJson must be a JSON object.";
        }
        catch (JsonException)
        {
            return "draftJson must be valid JSON.";
        }
    }
}
