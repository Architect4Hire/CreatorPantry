using System.Globalization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>One thing the server noticed about a brand-guide answer that the model did not report itself.</summary>
/// <param name="Code">A stable code, so a client can group findings without parsing the message.</param>
public sealed record AiBrandGuideFinding(
    AiBrandGuideDimension? Dimension, AiWarningKind Kind, string Code, string Message);

/// <summary>
/// The server's own read of a brand-guide answer: how much of a source passage it reproduces, and whether it
/// names or profiles a person.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two restrictions, two different strengths, and the difference is stated rather than blurred.</strong>
/// <see cref="LongestQuotedRun"/> is a measurement: it compares word runs and the answer either exceeds the
/// limit or does not, which is why the validator can refuse on it. <see cref="Scan"/> is a heuristic over a
/// bounded vocabulary and one naming convention, so it produces warnings a creator reads — it is not, and is not
/// presented as, proof that no writer was imitated and no trait inferred.
/// </para>
/// <para>
/// The structural half of both restrictions is the schema: <see cref="AiBrandGuideOutputDocument"/> has no field
/// for a quotation, a person or an attribute of one. This finds what a model wrote into prose anyway.
/// </para>
/// </remarks>
public static class AiBrandGuideClaimScanner
{
    public const string PassageQuoted = "brandGuide.passage_quoted";

    public const string PersonNamed = "brandGuide.person_named";

    public const string SensitiveTrait = "brandGuide.sensitive_trait";

    public const string SparseEvidence = "brandGuide.sparse_evidence";

    public const string SourceUnavailable = "brandGuide.source_unavailable";

    public const string UnsupportedGuidance = "brandGuide.unsupported_guidance";

    /// <summary>
    /// Phrasings that ask for, or claim, the voice of a particular person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched on the construction rather than on any list of names, which is the only form of this that can
    /// work: a name list would be both endless and a thing this repository has no business holding. What is
    /// detectable is the construction — "in the style of <em>Capitalised Name</em>" and its close relatives — with
    /// a capitalised name following it as the signal.
    /// </para>
    /// <para>
    /// <strong>Two alternatives, because the triggers are not equally strong.</strong> A marker that only means
    /// imitation — "emulating", "à la", "reminiscent of" — is enough on its own. The weak ones, "in" and "like",
    /// need a style noun or "of" after them, or every "in New York" in a piece of legitimate guidance would
    /// raise a warning; a heuristic that fires constantly is one a creator learns to ignore.
    /// </para>
    /// <para>
    /// The trigger is case-insensitive through an inline group so a sentence-initial "In the style of…" is
    /// caught, while the name stays case-<em>sensitive</em>: <c>\p{Lu}</c> under
    /// <see cref="RegexOptions.IgnoreCase"/> matches lowercase too, which would throw away the one signal that
    /// distinguishes a person's name from an ordinary noun.
    /// </para>
    /// </remarks>
    private static readonly Regex ImitationConstruction = new(
        @"(?:"
            + @"\b(?i:emulat\w*|imitat\w*|mimick?\w*|channell?ing|modell?ed\s+(?:on|after)|reminiscent\s+of|à\s+la|a\s+la)\s+"
            + @"(?i:the\s+)?(?i:style|voice|tone|manner|cadence|writing)?(?:\s+of)?\s+"
            + @"|\b(?i:in|like)\s+(?i:the\s+)?"
                + @"(?:(?i:style|voice|tone|manner|cadence|writing)(?:\s+of)?|of)\s+"
            + @")"
            + @"(?<name>\p{Lu}[\p{L}'’-]+(?:\s+\p{Lu}[\p{L}'’-]+)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A possessive or attributive reference to a named person's way of writing, such as "Nigella's voice".
    /// </summary>
    private static readonly Regex NamedPossessive = new(
        @"\b(?<name>\p{Lu}[\p{L}'’-]+(?:\s+\p{Lu}[\p{L}'’-]+)*)['’]s\s+(?:style|voice|tone|cadence|prose|writing|manner)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Vocabulary that would only appear if the answer had inferred something about a person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Protected and quasi-protected attributes plus the inference verbs that usually introduce them. What is
    /// reported is an attribute <em>ascribed to the creator or the reader</em>, and requiring that lead-in is
    /// what keeps the vocabulary usable: a food blog may legitimately need to say how it writes about religion,
    /// disability or a national cuisine, and only the ascription makes it an inference about a person.
    /// </para>
    /// <para>
    /// <strong>The vocabulary is small on purpose and is not a boundary.</strong> It covers the categories the
    /// prompt names — including family and health, which it would otherwise contradict — and it will miss
    /// phrasings nobody thought of ("your readers are mostly young mothers" reaches it only through the
    /// ascription branch). This produces a warning a creator reads, never a refusal, precisely because it cannot
    /// be relied on to be complete.
    /// </para>
    /// </remarks>
    private static readonly Regex SensitiveInference = new(
        @"\b(?:(?:the|your)\s+(?:creator|author|writer|reader|readers|audience)\s+"
                + @"(?:is|are|seems?|appears?|sounds?|is\s+likely|are\s+likely|is\s+probably|are\s+probably|skew\w*|tend\w*)"
            + @"|(?:suggests?|implies|indicates?|reveals?)\s+(?:that\s+)?(?:the|she|he|they|your)\s*)"
            + @"[^.]{0,80}?\b(?<trait>male|female|man|woman|men|women|mother|mothers|father|fathers|parent|parents"
            + @"|wife|husband|widow|widowed|childless|gay|lesbian|queer|trans|transgender|married|single|divorced"
            + @"|pregnant|disabled|autistic|depressed|anxious|diabetic|ill|jewish|muslim|christian|catholic|hindu"
            + @"|buddhist|atheist|immigrant|refugee|black|white|asian|hispanic|latina|latino|elderly|retired|young"
            + @"|wealthy|poor|working[-\s]class|liberal|conservative|republican|democrat)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The longest run of consecutive words any written text shares with any source passage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Words rather than characters, folded to lowercase with punctuation dropped, so reformatting, re-casing or
    /// re-punctuating a quotation does not evade the measure. Zero when either side has no words.
    /// </para>
    /// <para>
    /// Implemented as a rolling set of n-gram hashes per candidate length rather than a full suffix structure: the
    /// inputs are bounded by <see cref="AiPolicy.BrandGuideBodyMaxLength"/> and
    /// <see cref="CreatorPantry.Domain.Modules.Brand.Managers.BrandPolicy.MaxGroundingPassages"/>, so the simple
    /// form is fast enough and is the one a reader can check. It stops as soon as a run exceeds the limit the
    /// caller cares about, since the exact length past that point changes no decision.
    /// </para>
    /// </remarks>
    public static int LongestQuotedRun(IEnumerable<string> written, IEnumerable<string> passages)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(passages);

        var sources = passages.Select(Words).Where(words => words.Length > 0).ToList();

        if (sources.Count == 0)
        {
            return 0;
        }

        var ceiling = AiPolicy.BrandGuideMaxQuotedWordRun + 1;
        var longest = 0;

        foreach (var answer in written.Select(Words).Where(words => words.Length > 0))
        {
            // Grow the run length only while the shorter one was found: a run of n+1 cannot exist without a run
            // of n, so the first length that matches nothing ends the search for this text.
            for (var length = longest + 1; length <= Math.Min(answer.Length, ceiling); length++)
            {
                if (!SharesRun(answer, sources, length))
                {
                    break;
                }

                longest = length;

                if (longest >= ceiling)
                {
                    return longest;
                }
            }
        }

        return longest;
    }

    /// <summary>
    /// What the server has to say about one answer beyond what its own warnings say.
    /// </summary>
    /// <param name="unsupportedDimensions">
    /// Each item whose guidance rests on nothing, from the answer's own evidence basis, named by its dimension —
    /// or <c>null</c> for an item that has none, such as a rule. Reported so a creator is never shown an
    /// unsupported suggestion that looks like a finding. Deduplicated, so several unsupported rules produce one
    /// warning rather than one each.
    /// </param>
    /// <param name="passagesRead">How many source passages the request supplied.</param>
    /// <param name="unavailableSources">How many selected document versions could supply none.</param>
    public static IReadOnlyList<AiBrandGuideFinding> Scan(
        AiBrandGuideOutputDocument document,
        IEnumerable<AiBrandGuideDimension?> unsupportedDimensions,
        int passagesRead,
        int unavailableSources)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(unsupportedDimensions);

        var findings = new List<AiBrandGuideFinding>();

        foreach (var (dimension, text) in Written(document))
        {
            if (ImitationConstruction.Match(text) is { Success: true } || NamedPossessive.Match(text) is { Success: true })
            {
                findings.Add(new AiBrandGuideFinding(
                    dimension,
                    AiWarningKind.UnverifiedClaim,
                    PersonNamed,

                    // The matched name is deliberately not repeated: echoing it would put the thing the
                    // restriction is about into a stored row and into whatever renders it.
                    "This guidance appears to describe a particular person's way of writing. Brand guidance "
                        + "should describe how you write, not imitate someone else."));
            }

            if (SensitiveInference.IsMatch(text))
            {
                findings.Add(new AiBrandGuideFinding(
                    dimension,
                    AiWarningKind.UnverifiedClaim,
                    SensitiveTrait,
                    "This guidance appears to infer something personal about you or your readers from the source "
                        + "material. Nothing here can know that; read it before you keep it."));
            }
        }

        // Stated by the server, whatever the answer says about itself: an answer looks better without this, so
        // it cannot be the thing that decides whether it is said.
        if (passagesRead < AiPolicy.BrandGuideSparseEvidenceFloor)
        {
            findings.Add(new AiBrandGuideFinding(
                null,
                AiWarningKind.Limitation,
                SparseEvidence,
                passagesRead == 0
                    ? "No source passages were available, so this proposal rests on your guide answers alone."
                    : $"Only {passagesRead} source passage(s) were available, so the evidence behind this proposal is thin."));
        }

        if (unavailableSources > 0)
        {
            findings.Add(new AiBrandGuideFinding(
                null,
                AiWarningKind.Limitation,
                SourceUnavailable,
                $"{unavailableSources} of the documents you selected could not be read — their text may not be "
                    + "extracted or indexed yet — so nothing in this proposal comes from them."));
        }

        foreach (var dimension in unsupportedDimensions.Distinct())
        {
            findings.Add(new AiBrandGuideFinding(
                dimension,
                AiWarningKind.Assumption,
                UnsupportedGuidance,
                "This guidance rests on neither your answers nor your source material. It is a suggestion to "
                    + "judge, not something found in what you gave."));
        }

        return findings;
    }

    /// <summary>
    /// Everything the answer offered with nothing behind it, named by dimension where it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Rules as well as sections.</strong> A rule claiming <see cref="AiBrandGuideEvidence.None"/> is as
    /// unsupported as a section claiming it and is shown to the creator the same way; covering only sections
    /// would leave a "never do X" suggestion looking like a finding. A rule carries no dimension, so it reports
    /// <c>null</c> rather than having one invented for it.
    /// </para>
    /// <para>
    /// Here rather than in the handler so the evaluation harness derives this the same way the handler does. A
    /// fixture that rebuilt the argument itself would demonstrate the scanner against a copy of the rule, and
    /// pass happily after the real one changed.
    /// </para>
    /// </remarks>
    public static IEnumerable<AiBrandGuideDimension?> Unsupported(AiBrandGuideOutputDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.Sections
            .Where(section => section.Evidence is AiBrandGuideEvidence.None)
            .Select(section => (AiBrandGuideDimension?)section.Dimension)
            .Concat(document.Rules
                .Where(rule => rule.Evidence is AiBrandGuideEvidence.None)
                .Select(_ => (AiBrandGuideDimension?)null));
    }

    /// <summary>
    /// Every piece of prose the answer wrote, with the dimension it belongs to when it has one.
    /// </summary>
    /// <remarks>
    /// <strong><see cref="AiBrandGuideOutputDocument.Warnings"/> is included, and that is not incidental.</strong>
    /// A warning is free prose the creator reads like any other, so leaving it out would have given a quoted
    /// passage, a named writer or an inferred trait one field to live in where nothing looked. It is scanned
    /// before the server's own findings are appended, so only the model's own warnings are ever examined.
    /// </remarks>
    public static IEnumerable<(AiBrandGuideDimension? Dimension, string Text)> Written(
        AiBrandGuideOutputDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var section in document.Sections)
        {
            yield return (section.Dimension, section.Body);
        }

        foreach (var rule in document.Rules)
        {
            yield return (null, rule.Text);
        }

        foreach (var conflict in document.Conflicts)
        {
            yield return (conflict.Dimension, conflict.Summary);
        }

        foreach (var uncertainty in document.Uncertainties)
        {
            yield return (uncertainty.Dimension, uncertainty.Summary);
        }

        foreach (var warning in document.Warnings)
        {
            yield return (warning.Dimension, warning.Message);
        }
    }

    private static bool SharesRun(string[] answer, List<string[]> sources, int length)
    {
        var runs = new HashSet<string>(StringComparer.Ordinal);

        for (var start = 0; start + length <= answer.Length; start++)
        {
            runs.Add(string.Join(' ', answer, start, length));
        }

        foreach (var source in sources)
        {
            for (var start = 0; start + length <= source.Length; start++)
            {
                if (runs.Contains(string.Join(' ', source, start, length)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The text as comparable words: runs of letters and digits, lowercased, with everything else dropped.
    /// </summary>
    /// <remarks>
    /// Built by walking the string rather than by splitting on a separator set, so the tokenization depends only
    /// on each character's category and is identical for every input. Deriving separators from the text itself
    /// would tokenize an answer and a passage by different rules, and two strings that differ only in punctuation
    /// would stop comparing equal — which is precisely the evasion this measure has to be immune to.
    /// </remarks>
    private static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var words = new List<string>();
        var word = new System.Text.StringBuilder();

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLower(character, CultureInfo.InvariantCulture));
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return [.. words];
    }
}
