namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// The names of the picture quality classes, and the one rule for matching them (PRICE-006).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the names are in the shared kernel and the type is not.</strong> The classes are Media's: the
/// typed form is <c>GeneratedImageQualityClass</c>, and what each one sends is Media's configuration. But two
/// other things name them in text — a plan's <c>Billing:Plans:*:QualityClasses</c>, written by hand in the
/// landing page's spelling, and the landing page itself — and neither has any business reaching into Media to
/// learn how a class is spelled. A vocabulary is exactly what the kernel is for: no module depends on another
/// to use it, and nothing here knows what a class does.
/// </para>
/// <para>
/// <strong>Case and hyphens carry no meaning.</strong> The catalogue writes <c>high-2k</c> because an owner
/// edits it; the enum spells the same class <c>High2K</c>. AP.4.3 settled that neither spelling wins:
/// <see cref="Normalise"/> is the comparison, and it is the only one. A second copy of it is the drift this
/// file exists to prevent.
/// </para>
/// <para>
/// <c>PictureQualityVocabularyTests</c> holds <see cref="Names"/> equal to Media's enum, so a class added there
/// and not here fails a test rather than becoming a quality the catalogue can never name.
/// </para>
/// </remarks>
public static class PictureQualityVocabulary
{
    /// <summary>
    /// Every class that exists, in ascending order of quality — which is the order every list of them is given
    /// in, and the order a plan's classes are published in.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = ["Draft", "Standard", "High", "High2K", "High4K"];

    /// <summary>
    /// A class name reduced to what identifies it: upper case, with hyphens dropped.
    /// </summary>
    /// <remarks>
    /// Trimmed as well, because a configuration value edited by hand picks up spaces and a plan listing
    /// <c>" standard "</c> means the class, not a typo to refuse.
    /// </remarks>
    public static string Normalise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    /// <summary>Whether <paramref name="name"/> names a class at all, however it is spelled.</summary>
    public static bool IsKnown(string name) =>
        Names.Any(known => Normalise(known) == Normalise(name));

    /// <summary>
    /// The canonical spelling of <paramref name="name"/>, or null when it names no class.
    /// </summary>
    /// <remarks>
    /// What a module publishes when it has read a class name out of configuration: one spelling reaches every
    /// reader, so nothing downstream has to normalise anything.
    /// </remarks>
    public static string? Canonical(string name) =>
        Names.FirstOrDefault(known => Normalise(known) == Normalise(name));

    /// <summary>
    /// Which of <see cref="Names"/> appear in <paramref name="names"/>, canonically spelled and in ascending
    /// order whatever order they were written in. A name matching no class is left out.
    /// </summary>
    public static IReadOnlyList<string> CanonicalAll(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var asked = names.Select(Normalise).ToHashSet(StringComparer.Ordinal);

        return [.. Names.Where(known => asked.Contains(Normalise(known)))];
    }
}
