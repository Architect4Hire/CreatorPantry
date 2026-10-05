namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The three facts a prompt download is built from, projected in SQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not the entity.</strong> <c>IPromptRecordRepository.FindAsync</c> reads the whole row because the
/// detail route publishes nearly all of it; a text download publishes one column, so reading a 4,000-character
/// <c>GeneratedText</c> and a template triple out of the database to discard them would be work done for
/// nothing. The two columns that never leave the server — <c>WorkspaceId</c> and
/// <c>CreatedByMembershipId</c> — are not merely dropped here, they are never read.
/// </para>
/// <para>
/// <paramref name="Label"/> and <paramref name="CreatedAt"/> are here for the file name and for nothing else:
/// neither appears in the body, which is the prompt and only the prompt. See
/// <see cref="PromptDownloadFileName"/>.
/// </para>
/// </remarks>
public sealed record PromptTextRecord(string Text, string? Label, DateTimeOffset CreatedAt);

/// <summary>
/// A prompt as a file: the text to send, and the name to offer it under.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two fields, and the shape is the restriction.</strong> PRM-004 returns authorized prompt text, so
/// there is nowhere here to put a channel, a kind, a label, a template triple, a proposal id or a timestamp —
/// a download that carried provenance would be PRM-005's JSON record in a <c>.txt</c> file. A client that
/// wants any of it already has the detail route.
/// </para>
/// <para>
/// <strong>No workspace id and no membership id</strong>, which never leave the server (tenancy.md); and
/// <strong>no URL, no object path and no bytes</strong>, because a prompt record holds none (media.md).
/// <c>PromptTextDownloadTests</c> fails on a field named like any of them.
/// </para>
/// </remarks>
/// <param name="FileName">
/// A safe, deterministic ASCII file name for saving it. Never a path, and it carries no id, no workspace and
/// no storage location — see <see cref="PromptDownloadFileName"/>.
/// </param>
/// <param name="Text">
/// The authoritative prompt, exactly as stored — so trimmed, and with the creator's own interior line breaks.
/// Byte for byte what <see cref="PromptDetailServiceModel.Text"/> publishes: nothing is added, not even a
/// trailing newline, so "only the prompt text" is literally true rather than nearly true.
/// </param>
public sealed record PromptTextDownloadServiceModel(string FileName, string Text);

/// <summary>
/// A prompt as a JSON file: the document to send, and the name to offer it under.
/// </summary>
/// <remarks>
/// <para>
/// Two fields, as <see cref="PromptTextDownloadServiceModel"/> has, and for the same reason: everything the
/// download publishes is inside <paramref name="Json"/>, decided line by line by
/// <see cref="PromptRecordExportWriter"/>. A field added to this record would be a field outside the document
/// the schema version describes.
/// </para>
/// <para>
/// <strong>No workspace id and no membership id</strong> (tenancy.md), and <strong>no URL, object key or
/// storage path</strong> — a prompt record holds none (media.md), so there is nothing of the kind for the
/// export to exclude. <c>PromptRecordExportTests</c> scans the document's own keys for both.
/// </para>
/// </remarks>
/// <param name="FileName">
/// A safe, deterministic ASCII file name, as <see cref="PromptDownloadFileName"/> composes it — the same slug
/// and stamp the text download uses, with a <c>.json</c> extension.
/// </param>
/// <param name="Json">
/// The whole document and nothing else, so it can be written straight to a file. The same prompt produces the
/// same bytes every time.
/// </param>
public sealed record PromptRecordExportServiceModel(string FileName, string Json);
