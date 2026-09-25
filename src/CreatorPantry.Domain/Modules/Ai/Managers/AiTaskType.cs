namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Which AI capability an operation is running.</summary>
/// <remarks>
/// <para>
/// Deliberately almost empty. The recipe capabilities — concepts, rewrites, substitutions, scaling review —
/// arrive with the prompts that define them, and inventing their names here before a single one exists would
/// fix a vocabulary nothing has had to live with yet.
/// </para>
/// <para>
/// A task type is chosen by the server from an allow-list, never taken from a request field: the client picks
/// a discriminator the API recognises, and the API maps it here.
/// </para>
/// </remarks>
public enum AiTaskType
{
    /// <summary>
    /// Not declared. Never valid on a stored row — the check constraint refuses it — so that a row which
    /// forgot to say what it was doing cannot pass for the first real member of this enum.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// An inert task used to exercise the operation lifecycle end to end without calling a model. It is what
    /// the first generic proposal endpoint is wired to before any real capability is enabled.
    /// </summary>
    Diagnostic = 1,
}
