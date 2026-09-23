using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Reads and writes the stored form of a <see cref="RecipeSnapshotDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// The options here are part of the storage contract, not a style preference: changing any of them changes
/// how every previously written snapshot reads back. They are fixed deliberately rather than inherited from
/// an ambient default that could be reconfigured elsewhere in the process.
/// </para>
/// <para>
/// Enums are written as numbers, not names. A snapshot is an archive: a member renamed in C# next year must
/// not change what a document written today means, and the numeric values are already pinned by the check
/// constraints and filtered index the aggregate's configuration declares.
/// </para>
/// <para>
/// Nulls are written rather than omitted, so "the creator cleared this field" and "this field did not exist
/// when the snapshot was taken" stay distinguishable — the distinction shadow tables could not keep, and the
/// reason this is a document at all.
/// </para>
/// </remarks>
public static class RecipeSnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Indentation would inflate every stored row for a readability nobody reads it with.
        WriteIndented = false,
    };

    public static string Serialize(RecipeSnapshotDocument document) =>
        JsonSerializer.Serialize(document, Options);

    /// <summary>Reads a stored document.</summary>
    /// <remarks>
    /// Every failure arrives as <see cref="InvalidOperationException"/>, including malformed JSON and a
    /// payload that is valid JSON but not a snapshot. The underlying <c>JsonException</c> is deliberately not
    /// allowed to escape: a caller translating storage failures into a <c>ProblemDetails</c> result with a
    /// stable error code (backend.md) would otherwise have to know System.Text.Json's exception hierarchy to
    /// catch the case that actually happens, and a corrupt archive row would surface as an unhandled 500.
    /// The original is kept as the inner exception.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The stored text could not be read as a snapshot.</exception>
    public static RecipeSnapshotDocument Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RecipeSnapshotDocument>(json, Options)
                ?? throw new InvalidOperationException("A recipe snapshot deserialized to null; the stored document is not a snapshot.");
        }
        catch (JsonException failure)
        {
            throw new InvalidOperationException("The stored text could not be read as a recipe snapshot.", failure);
        }
    }
}
