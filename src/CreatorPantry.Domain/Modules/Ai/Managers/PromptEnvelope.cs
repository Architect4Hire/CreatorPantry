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
    internal PromptEnvelope(string nonce, string systemMessage, string userMessage, IReadOnlyList<PromptSegment> segments)
    {
        Nonce = nonce;
        SystemMessage = systemMessage;
        UserMessage = userMessage;
        Segments = segments;
    }

    /// <summary>The fence token for this envelope. Fresh per build, and never reused.</summary>
    public string Nonce { get; }

    /// <summary>The instruction segments: policy, task, output schema.</summary>
    public string SystemMessage { get; }

    /// <summary>The data segments, and the closing reminder.</summary>
    public string UserMessage { get; }

    public IReadOnlyList<PromptSegment> Segments { get; }

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

        return description.ToString();
    }
}

/// <summary>
/// An envelope that could not be built safely: a missing required segment, content from the wrong workspace,
/// or content carrying this envelope's own fence token.
/// </summary>
/// <remarks>
/// An exception rather than a result type. Every one of these is a server-side assembly fault — the caller
/// chose the segments and loaded their content — and none is something a creator could cause or resolve.
/// </remarks>
public sealed class PromptEnvelopeException(string message) : InvalidOperationException(message);
