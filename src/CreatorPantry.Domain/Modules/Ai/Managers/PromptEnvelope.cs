using System.Text;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// A built prompt: a system message carrying the instructions and a user message carrying the data, every
/// segment fenced with a per-envelope token.
/// </summary>
/// <remarks>
/// <para>
/// The split is the primary defence and the fences are the secondary one. Instructions live in the system
/// role and data in the user role, so the separation survives even a model that pays no attention to the
/// fences; the fences then say which data is which, and whose it is.
/// </para>
/// <para>
/// <strong>What this can and cannot promise.</strong> It guarantees that untrusted bytes arrive inside an
/// untrusted fence and nowhere else, unmodified, and that nothing in the content can forge a boundary. It
/// cannot guarantee a model declines an instruction it finds in the data — that is behaviour, and it belongs
/// to the evaluation harness. Treating structural separation as if it were behavioural compliance is the
/// mistake this remark exists to prevent.
/// </para>
/// </remarks>
public sealed class PromptEnvelope
{
    internal PromptEnvelope(
        string nonce,
        string systemMessage,
        string userMessage,
        IReadOnlyList<PromptSegment> segments,
        IReadOnlyList<PromptImage>? images = null)
    {
        Nonce = nonce;
        SystemMessage = systemMessage;
        UserMessage = userMessage;
        Segments = segments;
        Images = images ?? [];
    }

    /// <summary>The fence token for this envelope. Fresh per build, and never reused.</summary>
    public string Nonce { get; }

    /// <summary>The instruction segments: policy, task, output schema.</summary>
    public string SystemMessage { get; }

    /// <summary>The data segments, and the closing reminder.</summary>
    public string UserMessage { get; }

    public IReadOnlyList<PromptSegment> Segments { get; }

    /// <summary>
    /// The images attached to the user message, in the order they were added. Empty for every text-only
    /// capability.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Segments"/> because pixels cannot be fenced in a text message: each image has
    /// a <see cref="PromptSegmentKind.ReferenceImage"/> segment announcing it, and the bytes travel here. A
    /// gateway with no images to attach composes exactly the message it always did.
    /// </remarks>
    public IReadOnlyList<PromptImage> Images { get; }

    /// <summary>
    /// What may be logged about this envelope: which segments it carried, their trust, and how large they
    /// were.
    /// </summary>
    /// <remarks>
    /// No content, no nonce, no workspace id. <c>ai.md</c> forbids logging prompt bodies and cross-workspace
    /// material by default, and a describe method that returned content would be the obvious way for one to
    /// reach a log. The nonce is omitted because it has no diagnostic value and appears verbatim in the prompt
    /// it fences.
    /// </remarks>
    public string Describe()
    {
        var description = new StringBuilder("segments=").Append(Segments.Count);

        foreach (var segment in Segments)
        {
            description.Append("; ")
                .Append(PromptEnvelopePolicy.FenceNameOf(segment.Kind))
                .Append('/')
                .Append(segment.Trust)
                .Append('=')
                .Append(segment.Content.Length)
                .Append("ch");
        }

        foreach (var image in Images)
        {
            description.Append("; ").Append(image.MediaType).Append('=').Append(image.Bytes.Length).Append('B');
        }

        return description.ToString();
    }
}

/// <summary>
/// One image attached to an envelope's user message.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The bytes are carried, never logged and never serialized into the prompt text.</strong>
/// <see cref="PromptEnvelope.Describe"/> reports the media type and the length and nothing else, for the same
/// reason it reports no segment content: <c>ai.md</c> forbids a prompt body reaching a log, and a reference
/// image is a creator's own photograph.
/// </para>
/// <para>
/// <paramref name="WorkspaceId"/> is checked against the envelope's at build time, exactly as a text
/// segment's is. Retrieval returning a neighbour's image is the realistic failure, and the builder is the
/// last place it can be caught before the content reaches a model.
/// </para>
/// </remarks>
/// <param name="MediaType">The type established from the bytes at upload, never one a client declared.</param>
public sealed record PromptImage(ReadOnlyMemory<byte> Bytes, string MediaType, Guid WorkspaceId);

/// <summary>
/// An envelope that could not be built safely: a missing required segment, content from the wrong workspace,
/// or content carrying this envelope's own fence token.
/// </summary>
/// <remarks>
/// An exception rather than a result type. Every one of these is a server-side assembly fault — the caller
/// chose the segments and loaded their content — and none is something a creator could cause or resolve.
/// </remarks>
public sealed class PromptEnvelopeException(string message) : InvalidOperationException(message);
