using System.Security.Cryptography;
using System.Text;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Assembles a <see cref="PromptEnvelope"/> for one workspace.
/// </summary>
/// <remarks>
/// <para>
/// Every method that adds non-instruction content takes the workspace the content was read from, and
/// <see cref="Build()"/> refuses if any of them disagrees with the workspace this builder was created for. That
/// makes "never ground a prompt with another workspace's data" a property of the builder rather than a rule
/// reviewers check: a retrieval bug that returned a neighbour's snapshot fails here, loudly, instead of
/// reaching a model. The friction of passing the id at each call site is the feature.
/// </para>
/// <para>
/// Content is never altered. No escaping, no stripping of fence-looking lines, no normalisation — recipes.md
/// makes creator-entered text canonical, and mangling it to defend a boundary the nonce already defends would
/// trade a real guarantee for a cosmetic one.
/// </para>
/// </remarks>
public sealed class PromptEnvelopeBuilder
{
    private readonly Guid _workspaceId;
    private readonly List<PromptSegment> _segments = [];

    /// <param name="workspaceId">The resolved workspace. Every non-instruction segment must match it.</param>
    public PromptEnvelopeBuilder(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new PromptEnvelopeException(
                "An envelope needs a resolved workspace. Building one for Guid.Empty would disable the "
                + "cross-workspace check entirely.");
        }

        _workspaceId = workspaceId;
    }

    /// <summary>The rendered prompt template body. Required.</summary>
    public PromptEnvelopeBuilder WithTask(string body) => Add(PromptSegmentKind.Task, body, null);

    /// <summary>The JSON Schema the answer must satisfy. Required.</summary>
    public PromptEnvelopeBuilder WithOutputSchema(string schema) =>
        Add(PromptSegmentKind.OutputSchema, schema, null);

    /// <param name="workspaceId">The workspace these preferences were read from.</param>
    public PromptEnvelopeBuilder WithPreferences(Guid workspaceId, string content) =>
        Add(PromptSegmentKind.Preferences, content, workspaceId);

    /// <param name="workspaceId">The workspace this snapshot was read from.</param>
    public PromptEnvelopeBuilder WithSource(Guid workspaceId, string content) =>
        Add(PromptSegmentKind.Source, content, workspaceId);

    /// <param name="workspaceId">The workspace this reference was retrieved from.</param>
    public PromptEnvelopeBuilder AddReference(Guid workspaceId, string content) =>
        Add(PromptSegmentKind.References, content, workspaceId);

    /// <param name="workspaceId">The workspace this text was supplied to or imported into.</param>
    public PromptEnvelopeBuilder WithUntrustedText(Guid workspaceId, string content) =>
        Add(PromptSegmentKind.UntrustedText, content, workspaceId);

    /// <exception cref="PromptEnvelopeException">
    /// A required segment is missing, a segment came from another workspace, or content carries the fence
    /// token this envelope generated.
    /// </exception>
    public PromptEnvelope Build() =>
        Build(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PromptEnvelopePolicy.NonceBytes)));

    /// <summary>Builds with a caller-supplied token, so the forged-fence guard can be exercised directly.</summary>
    /// <remarks>
    /// Internal, and internal to tests only. A nonce that anything outside this class can choose is not a
    /// nonce, and the whole defence rests on it being unguessable — so the public entry point above is the
    /// only way application code reaches this.
    /// </remarks>
    internal PromptEnvelope Build(string nonce)
    {
        RequireSegment(PromptSegmentKind.Task);
        RequireSegment(PromptSegmentKind.OutputSchema);

        var ordered = new List<PromptSegment>
        {
            new(PromptSegmentKind.Policy, PromptEnvelopePolicy.SystemPolicy(nonce)),
        };

        // Fixed order, not insertion order: a caller cannot rearrange an envelope so that data precedes the
        // rules it is governed by, and the reminder is always last.
        ordered.AddRange(InOrder(
            PromptSegmentKind.Task,
            PromptSegmentKind.OutputSchema,
            PromptSegmentKind.Preferences,
            PromptSegmentKind.Source,
            PromptSegmentKind.References,
            PromptSegmentKind.UntrustedText));

        ordered.Add(new PromptSegment(PromptSegmentKind.Reminder, PromptEnvelopePolicy.Reminder));

        RequireNoForgedFences(ordered, nonce);

        var system = Render(ordered.Where(segment =>
            segment.Kind is PromptSegmentKind.Policy or PromptSegmentKind.Task or PromptSegmentKind.OutputSchema),
            nonce);

        var user = Render(ordered.Where(segment =>
            segment.Kind is not (PromptSegmentKind.Policy or PromptSegmentKind.Task or PromptSegmentKind.OutputSchema)),
            nonce);

        return new PromptEnvelope(nonce, system, user, ordered);
    }

    private PromptEnvelopeBuilder Add(PromptSegmentKind kind, string content, Guid? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(content);

        var trust = PromptEnvelopePolicy.TrustOf(kind);

        if (trust is not PromptSegmentTrust.Instruction)
        {
            if (workspaceId is null || workspaceId == Guid.Empty)
            {
                throw new PromptEnvelopeException(
                    $"The {PromptEnvelopePolicy.FenceNameOf(kind)} segment must say which workspace its "
                    + "content was read from.");
            }

            if (workspaceId != _workspaceId)
            {
                // The check that matters. Retrieval returning a neighbour's row is the realistic failure, and
                // this is the last place it can be caught before the content reaches a model.
                throw new PromptEnvelopeException(
                    $"The {PromptEnvelopePolicy.FenceNameOf(kind)} segment was read from workspace "
                    + $"{workspaceId} but this envelope is for {_workspaceId}. A prompt is never grounded "
                    + "with another workspace's material.");
            }
        }

        // References accumulate; everything else is singular, and a second one is a caller bug rather than an
        // append.
        if (kind is not PromptSegmentKind.References && _segments.Any(segment => segment.Kind == kind))
        {
            throw new PromptEnvelopeException(
                $"The {PromptEnvelopePolicy.FenceNameOf(kind)} segment was supplied twice.");
        }

        _segments.Add(new PromptSegment(kind, content, workspaceId));

        return this;
    }

    private void RequireSegment(PromptSegmentKind kind)
    {
        if (!_segments.Any(segment => segment.Kind == kind))
        {
            throw new PromptEnvelopeException(
                $"An envelope needs a {PromptEnvelopePolicy.FenceNameOf(kind)} segment.");
        }
    }

    private IEnumerable<PromptSegment> InOrder(params PromptSegmentKind[] kinds) =>
        kinds.SelectMany(kind => _segments.Where(segment => segment.Kind == kind));

    /// <summary>
    /// Refuses content that carries this envelope's own fence token.
    /// </summary>
    /// <remarks>
    /// It cannot happen by chance — the token is 128 random bits. It can happen when a previous envelope is
    /// echoed back through retrieval or pasted into a recipe, and an envelope whose data contains its own
    /// fences has no single reading. Failing is the only honest response; the alternative is emitting
    /// something whose structure is ambiguous to the model and to us.
    /// </remarks>
    private static void RequireNoForgedFences(IEnumerable<PromptSegment> segments, string nonce)
    {
        foreach (var segment in segments.Where(segment => segment.Trust is not PromptSegmentTrust.Instruction))
        {
            if (segment.Content.Contains(nonce, StringComparison.OrdinalIgnoreCase))
            {
                throw new PromptEnvelopeException(
                    $"The {PromptEnvelopePolicy.FenceNameOf(segment.Kind)} segment contains this envelope's "
                    + "fence token, so its boundaries would be ambiguous.");
            }
        }
    }

    private static string Render(IEnumerable<PromptSegment> segments, string nonce)
    {
        var rendered = new StringBuilder();

        foreach (var segment in segments)
        {
            var name = PromptEnvelopePolicy.FenceNameOf(segment.Kind);

            if (rendered.Length > 0)
            {
                rendered.Append('\n');
            }

            rendered.Append("-----BEGIN ").Append(name).Append(' ').Append(nonce).Append("-----\n")
                .Append(segment.Content).Append('\n')
                .Append("-----END ").Append(name).Append(' ').Append(nonce).Append("-----\n");
        }

        return rendered.ToString();
    }
}
