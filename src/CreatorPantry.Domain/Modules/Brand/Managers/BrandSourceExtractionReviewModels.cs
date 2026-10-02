using System.Text;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// One document version's text as a creator reads it for review: what state the extraction is in, the text if
/// there is any, and the identity the next correction has to quote.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Text"/> is present exactly when <see cref="State"/> is
/// <see cref="BrandSourceExtractionState.Succeeded"/></strong> — the same rule the extraction table enforces on
/// the row, restated on the wire so a client never has to ask whether an empty string means empty or absent.
/// <see cref="BrandSourceExtractionState.Unsupported"/> carries <see cref="Reason"/> instead, which is where a
/// scanned PDF says so, and is the case a creator answers by typing the text in themselves.
/// </para>
/// <para>
/// <strong>Never an object key, a container or a URL</strong>, and never a membership id. <see cref="Origin"/>
/// carries the fact that matters — whether a person or a parser produced this text — and the person stays on the
/// row and in the audit entry, as everywhere else in this module.
/// </para>
/// <para>
/// However it was produced, this text is untrusted: a creator's correction is no more an instruction than a
/// parser's output is, and both are delimited source material when they reach a prompt (ai.md).
/// </para>
/// </remarks>
/// <param name="VersionNumber">The version this text was read from, as the document's metadata numbers it.</param>
/// <param name="Id">
/// The artifact's identity, and the value a correction sends back as <c>expectedExtractionId</c>. Null only when
/// <see cref="State"/> is <see cref="BrandSourceExtractionState.NotExtracted"/> — there is no artifact to name.
/// </param>
/// <param name="Ordinal">
/// 1 for the version's first extraction, one more per retry or correction; 0 when there has been none. A count of
/// how many times this version's text has been written, which is provenance rather than a cursor.
/// </param>
/// <param name="Reason">
/// Why there is no text, or that there was more of it than is stored, or the creator's own note on a correction.
/// Null when there is nothing to say.
/// </param>
/// <param name="At">When this artifact was recorded. Null when there has been no extraction.</param>
/// <param name="IsReading">
/// Whether a read of this version is queued or running right now. Independent of <paramref name="State"/>: a
/// version whose last attempt failed and which a creator has asked to be read again is <c>Failed</c> and reading
/// until the new attempt settles, and a version nothing has finished reading is <c>NotExtracted</c> either way —
/// reading while the work is queued, not reading once it has stopped.
/// </param>
public sealed record BrandSourceExtractionServiceModel(
    int VersionNumber,
    BrandSourceExtractionState State,
    Guid? Id,
    int Ordinal,
    BrandSourceExtractionOrigin? Origin,
    string? Text,
    string? Reason,
    DateTimeOffset? At,
    bool IsReading = false)
{
    /// <summary>The answer for a version nothing has extracted yet: a state, and nothing else to report.</summary>
    public static BrandSourceExtractionServiceModel NotExtracted(int versionNumber) =>
        new(versionNumber, BrandSourceExtractionState.NotExtracted, null, 0, null, null, null, null);
}

/// <summary>
/// A creator's request to have one version's text read again.
/// </summary>
/// <remarks>
/// No field for a workspace, a document or a version: all three come from the route and the resolved context.
/// </remarks>
public sealed record RetryBrandSourceExtractionViewModel
{
    /// <summary>
    /// The <c>id</c> of the artifact the creator was looking at when they asked, or null when they were looking at
    /// a version nothing had read. The same concurrency check a correction makes: a read that has since landed
    /// changes what there is to retry.
    /// </summary>
    public Guid? ExpectedExtractionId { get; init; }
}

/// <summary>
/// A creator's correction of one version's extracted text.
/// </summary>
/// <remarks>
/// There is no field for a workspace, a document or a version: all three come from the route and the resolved
/// context. There is no field for a status either — a correction is always text a person supplied, so it is
/// always <see cref="BrandSourceExtractionStatus.Succeeded"/>, which the schema insists on for a
/// <see cref="BrandSourceExtractionOrigin.Corrected"/> row.
/// </remarks>
public sealed record CorrectBrandSourceExtractionViewModel
{
    /// <summary>
    /// The <c>id</c> of the artifact this correction was composed against. Required.
    /// </summary>
    /// <remarks>
    /// The concurrency check, and an identity rather than a row version because an extraction has none: it is
    /// immutable history, not an editable row. An identity rather than the ordinal for a second reason — an
    /// ordinal can be composed by arithmetic, so a caller who never read the text could name the next one, while
    /// this names an artifact nobody can guess.
    /// </remarks>
    public Guid? ExpectedExtractionId { get; init; }

    /// <summary>The corrected text, in full. Required.</summary>
    /// <remarks>
    /// The whole artifact, not a patch. A patch would need the server to apply it to text the creator may have
    /// been looking at a stale copy of, and the concurrency check above is what makes sending the whole thing
    /// safe instead.
    /// </remarks>
    public string? Text { get; init; }

    /// <summary>The creator's note on what was wrong and what they changed. Required.</summary>
    public string? Reason { get; init; }
}

public sealed class CorrectBrandSourceExtractionViewModelValidator
    : AbstractValidator<CorrectBrandSourceExtractionViewModel>
{
    public CorrectBrandSourceExtractionViewModelValidator()
    {
        RuleFor(model => model.ExpectedExtractionId)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Send the id of the extracted text you are correcting.")
            .NotEqual(Guid.Empty).WithMessage("That is not an extracted-text id this API issued.");

        // Custom rather than a chain of Musts, so the refusal the creator reads is the specific one: "too long"
        // and "carries invisible characters" have different remedies, and one told only "invalid" has to guess.
        RuleFor(model => model.Text).Custom((text, context) =>
        {
            if (!BrandSourceCorrectionText.TryNormalize(text, out _, out var failure))
            {
                context.AddFailure(nameof(CorrectBrandSourceExtractionViewModel.Text), failure!);
            }
        });

        RuleFor(model => model.Reason)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Say what you corrected, so the change can be explained later.")
            .MaximumLength(BrandPolicy.ReasonMaxLength);
    }
}

/// <summary>
/// What a creator may submit as corrected text, and the only reshaping it receives.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately not the parser's normalizer.</strong> <see cref="BrandSourceTextBuilder"/> and
/// <see cref="BrandSourcePlainTextExtractor"/> collapse blank runs and drop characters, which is right for text
/// a machine reconstructed from a binary format and wrong for text a person typed: recipes.md's rule about
/// keeping the creator's own words applies to a correction as much as to an ingredient line. What happens here is
/// only what the storage form requires — line endings to <c>\n</c>, no byte-order mark, one trailing newline —
/// and that is a format, not an edit.
/// </para>
/// <para>
/// <strong>Invisible characters are refused rather than stripped.</strong> A zero-width joiner or a
/// bidirectional override has no place in a stored artifact, but silently removing it would mean the creator's
/// text came back different from what they sent with nothing said about it. Telling them is the honest answer,
/// and it is also the one that does not quietly alter text a later prompt will treat as source material.
/// </para>
/// </remarks>
internal static class BrandSourceCorrectionText
{
    /// <summary>
    /// The storage form of a submitted correction, or why it cannot be accepted.
    /// </summary>
    /// <param name="normalized">The text exactly as it will be stored and checksummed.</param>
    /// <param name="failure">A creator-facing reason, present exactly when this returns false.</param>
    public static bool TryNormalize(string? text, out string normalized, out string? failure)
    {
        normalized = string.Empty;
        failure = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            failure = "Send the corrected text.";

            return false;
        }

        // A byte-order mark is a stream marker, not a character, and the one at the front is the only one a
        // paste is likely to carry.
        var body = text.TrimStart('﻿').ReplaceLineEndings("\n");

        foreach (var character in body)
        {
            if (character is '\n' or '\t')
            {
                continue;
            }

            if (char.IsControl(character)
                || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format)
            {
                failure = "That text carries invisible characters. Remove them and send it again.";

                return false;
            }
        }

        int bytes;

        try
        {
            // Throws on a lone surrogate, which is the one way a .NET string cannot be encoded at all. Caught
            // here rather than at the store, where it would be a write that failed for no stated reason.
            bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetByteCount(body);
        }
        catch (EncoderFallbackException)
        {
            failure = "That text is not valid Unicode.";

            return false;
        }

        if (bytes > BrandPolicy.ExtractedTextMaxBytes)
        {
            failure = "That text is longer than this workspace stores for one document. Shorten it and send it again.";

            return false;
        }

        // Trailing whitespace off and exactly one newline on, so the stored artifact ends on a line like any
        // text file and two submissions differing only in trailing blank lines are the same correction.
        var trimmed = body.TrimEnd();

        if (trimmed.Length == 0)
        {
            failure = "Send the corrected text.";

            return false;
        }

        normalized = trimmed + '\n';

        return true;
    }
}
