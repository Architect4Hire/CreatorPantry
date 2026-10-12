using System.Globalization;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>What kind of thing a finding about a section is.</summary>
public enum LongFormFindingKind
{
    /// <summary>A figure or a statement the sources do not support.</summary>
    UnverifiedClaim = 1,

    /// <summary>A claim about safety, health, diet, allergens, nutrition or keeping food.</summary>
    SafetyCaution = 2,

    /// <summary>Something that could not be checked, or a limit that was reached.</summary>
    Limitation = 3,
}

/// <summary>One thing the scanner found in a section's text. Stored with the section, in the revision.</summary>
/// <param name="Code">The stable code of the frame that raised it.</param>
/// <param name="Message">A sentence for the creator. It may quote the figure or phrase it is about.</param>
public sealed record LongFormSectionFinding(string Code, LongFormFindingKind Kind, string Message);

/// <summary>One pinned recipe version, as the scanner reads it.</summary>
/// <param name="ReferenceId">The creative-context reference that named the recipe.</param>
/// <param name="FactLines">Its ingredient lines, steps, yield and times, each as a line that states a figure.</param>
public sealed record LongFormClaimRecipe(Guid ReferenceId, string Title, IReadOnlyList<string> FactLines);

/// <summary>
/// The deterministic scanner for a long-form section's text (LF.3.2): every figure checked against the recipe
/// versions the revision is pinned to, and every claim frame the editorial and SEO scanners catch.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Code, not a model, and the same for every author.</strong> A body a model wrote and a body the
/// creator typed are read by the same rules. A finding is a caution shown beside the section: nothing here
/// rewrites, removes or refuses a word, and nothing that calls this may refuse a save on its say-so.
/// </para>
/// <para>
/// <strong>The frames are the existing scanners', asked rather than restated.</strong> Figures, safety,
/// allergen, dietary, medical and nutrition claims, storage and preservation advice, invented provenance and
/// search metrics are all <see cref="IAiClaimScanFacade"/>'s. What is added here is what only long-form needs:
/// which recipe a section answers to, a numbered list's own numbers, the storage section saying it has no
/// notes, and a promise that a recipe cannot fail.
/// </para>
/// <para>
/// <strong>A frame, not a word.</strong> "Never fails" is a promise; "most loaves fail because the oven was
/// cold" is a tip. A dish called by the name its creator gave it is not making a claim.
/// </para>
/// <para>
/// <strong>With no pinned recipe, every figure is unsupported.</strong> A section may be asked for a method or
/// a yield; with no recipe behind the piece, a quantity or a time in it can only have been made up.
/// </para>
/// </remarks>
public static partial class LongFormClaimScanner
{
    /// <summary>A section promises the recipe will work: foolproof, never fails, works every time.</summary>
    public const string UnsupportedGuarantee = "long_form.unsupported_guarantee";

    /// <summary>The editorial scanner's code for storage advice the sources do not support.</summary>
    private const string UnsupportedStorageGuidance = "editorial.unsupported_storage_guidance";

    /// <summary>
    /// The longest a storage section's body may be and still be read as saying only that there are no notes.
    /// </summary>
    public const int NoStorageNotesMaxLength = 240;

    /// <summary>
    /// What the scanner finds in one section's text.
    /// </summary>
    /// <param name="sourceReferenceId">
    /// The recipe a section is bound to, when it is — a newsletter item. It is then held to that recipe's
    /// figures alone: another recipe's oven temperature is a figure the piece states and this recipe does not.
    /// </param>
    /// <param name="recipes">The recipe versions the revision is pinned to. Empty when it names none.</param>
    public static IReadOnlyList<LongFormSectionFinding> Scan(
        IAiClaimScanFacade scanner,
        LongFormSectionRole role,
        Guid? sourceReferenceId,
        string? body,
        IReadOnlyList<LongFormClaimRecipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(recipes);

        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var answersTo = sourceReferenceId is { } bound && recipes.FirstOrDefault(recipe => recipe.ReferenceId == bound) is { } own
            ? [own]
            : recipes;

        // An empty source, never a missing one: with nothing to check a figure against, every figure is one
        // nothing supports. Titles are prose, so a dish may be called by its name.
        var source = new AiClaimSourceServiceModel(
            [.. answersTo.SelectMany(recipe => recipe.FactLines)],
            string.Join(' ', recipes.Select(recipe => recipe.Title)));

        // A numbered list's own numbers are not figures. Taken out of the copy that is read and of nothing
        // else: the body is stored as written.
        var text = WithoutListNumbers(body);
        var findings = new List<LongFormSectionFinding>();

        foreach (var found in scanner.Scan(text, source))
        {
            // The one thing a storage section may say about storage without being told so: that the creator
            // gave no notes. Everything else it says about keeping or reheating is advice, and is reported.
            if (role is LongFormSectionRole.Storage
                && found.Code == UnsupportedStorageGuidance
                && OnlySaysThereAreNoStorageNotes(scanner, body))
            {
                continue;
            }

            Add(findings, new LongFormSectionFinding(found.Code, KindOf(found.Kind), found.Message));
        }

        if (Guarantee().IsMatch(text))
        {
            Add(findings, new LongFormSectionFinding(
                UnsupportedGuarantee,
                LongFormFindingKind.UnverifiedClaim,
                "This section promises the recipe will work. Nothing in your recipe supports a promise."));
        }

        return findings;
    }

    /// <summary>
    /// The body with the numbers of its numbered lists taken out, and every other figure left where it was.
    /// </summary>
    /// <remarks>
    /// A marker is one only where it continues a count: 1, then 2, then 3, each at the start of a line. A line
    /// that merely opens with a figure — "375) is the temperature" — is not a list, and its figure is read.
    /// </remarks>
    public static string WithoutListNumbers(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var expected = 1;

        return ListNumber().Replace(body, match =>
        {
            var number = int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : -1;

            if (number != expected && number != 1)
            {
                return match.Value;
            }

            expected = number + 1;

            return string.Empty;
        });
    }

    /// <summary>
    /// Whether a storage section's body says only that the creator gave no storage notes.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. In prose, "reheat gently in a low oven" is advice made of exactly the words the
    /// section is named for. So the body must be short, must say in so many words that there are no notes, and
    /// must have nothing else the storage check answers to once the section's own name is set aside.
    /// </remarks>
    public static bool OnlySaysThereAreNoStorageNotes(IAiClaimScanFacade scanner, string body)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(body);

        return body.Length <= NoStorageNotesMaxLength
            && NoStorageNotes().IsMatch(body)
            && scanner.Scan(StorageName().Replace(body, " "), source: null)
                .All(finding => finding.Code != UnsupportedStorageGuidance);
    }

    private static void Add(List<LongFormSectionFinding> findings, LongFormSectionFinding finding)
    {
        // The same sentence twice says nothing the first did not.
        if (!findings.Contains(finding))
        {
            findings.Add(finding);
        }
    }

    private static LongFormFindingKind KindOf(AiWarningKind kind) => kind switch
    {
        AiWarningKind.SafetyCaution or AiWarningKind.CulinaryCaution => LongFormFindingKind.SafetyCaution,
        AiWarningKind.Limitation or AiWarningKind.UnresolvedQuestion => LongFormFindingKind.Limitation,
        _ => LongFormFindingKind.UnverifiedClaim,
    };

    [GeneratedRegex(@"^[ \t]*(\d{1,3})[.)][ \t]+", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ListNumber();

    [GeneratedRegex(@"\b(?:stor(?:e|es|ed|ing|age)|reheat\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StorageName();

    [GeneratedRegex(
        @"\b(?:has|have|had)\s+not\s+(?:given|shared|written|provided|included|supplied)\b[^.!?]*\bnotes?\b"
            + @"|\b(?:hasn't|haven't|did\s+not|didn't)\s+(?:give|given|share|shared|write|written|provide|provided|include|included)\b[^.!?]*\bnotes?\b"
            + @"|\bno\s+(?:storage|storing|keeping|reheating)[^.!?]*\bnotes?\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NoStorageNotes();

    [GeneratedRegex(
        @"\bfool-?proof\b|\bfail-?(?:proof|safe)\b|\bnever\s+fails?\b|\b(?:cannot|can't|can’t|won't|won’t)\s+(?:fail|go\s+wrong)\b"
            + @"|\bworks?\s+every\s+(?:single\s+)?time\b|\bperfect\s+every\s+time\b|\balways\s+(?:works|turns\s+out)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Guarantee();
}
