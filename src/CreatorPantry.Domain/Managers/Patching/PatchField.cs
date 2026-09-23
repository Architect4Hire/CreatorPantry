using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Managers.Patching;

/// <summary>
/// One field of a partial-update request, carrying the distinction a nullable type cannot: whether the
/// caller mentioned this field at all, and — separately — whether they asked for it to be cleared.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The three states.</strong> Absent means "leave this alone". Submitted with a value means "set it
/// to this". Submitted with <c>null</c> means "clear it". A plain <c>string?</c> collapses the first and the
/// third into one, which is exactly the ambiguity that makes hand-rolled PATCH endpoints silently destroy
/// data: a client that omits a field it does not know about has the server blank it.
/// </para>
/// <para>
/// <strong>How absence is detected, and why it needs no ceremony.</strong> System.Text.Json invokes a
/// converter only for a property that is <em>present</em> in the document. A property the body never
/// mentioned is never touched, so it keeps <c>default(PatchField&lt;T&gt;)</c> — whose
/// <see cref="IsSubmitted"/> is <c>false</c>. Nothing has to track a list of seen fields, and nothing can
/// forget to.
/// </para>
/// <para>
/// <strong>Request-only.</strong> These types describe what a caller asked for; responses are ServiceModels
/// that state what is. <see cref="PatchFieldJsonConverterFactory"/> refuses to write one, because a
/// converter cannot omit a property and so could only spell "absent" as some value that is really a
/// different request.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// The field's own type, nullability included: <c>PatchField&lt;string?&gt;</c> is a clearable field and
/// <c>PatchField&lt;string&gt;</c> is one that may be set but never cleared.
/// </typeparam>
[JsonConverter(typeof(PatchFieldJsonConverterFactory))]
public readonly struct PatchField<T>
{
    private readonly T _value;

    private PatchField(T value)
    {
        IsSubmitted = true;
        _value = value;
    }

    /// <summary>True when the request mentioned this field, whatever it said about it.</summary>
    public bool IsSubmitted { get; }

    /// <summary>
    /// What the request asked this field to become — <c>null</c> when it asked for it to be cleared.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The field was never submitted. Reading a value out of a field nobody mentioned is always a mistake,
    /// and returning <c>default</c> would turn it into a silent one: for a clearable field, the default is
    /// indistinguishable from an explicit clear. <see cref="Or"/> is the safe way to ask.
    /// </exception>
    public T Value => IsSubmitted
        ? _value
        : throw new InvalidOperationException("This field was not submitted, so it has no requested value.");

    /// <summary>A field the request did not mention.</summary>
    public static PatchField<T> Absent => default;

    /// <summary>A field the request mentioned, asking for <paramref name="value"/> — <c>null</c> to clear it.</summary>
    public static PatchField<T> Submitted(T value) => new(value);

    /// <summary>
    /// The value this field should end up holding: what was submitted, or <paramref name="current"/> when
    /// nothing was.
    /// </summary>
    /// <remarks>
    /// The whole merge is written with this, one line per field, so "unsubmitted fields remain unchanged"
    /// is the shape of the code rather than a rule someone has to keep applying.
    /// </remarks>
    public T Or(T current) => IsSubmitted ? _value : current;

    /// <summary>Reads the submitted value, if there is one.</summary>
    public bool TryGetSubmitted([MaybeNullWhen(false)] out T value)
    {
        value = _value;

        return IsSubmitted;
    }
}
