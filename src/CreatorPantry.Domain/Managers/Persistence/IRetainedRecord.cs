namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// Marks an entity whose rows are kept: they may be updated only as the entity itself allows, and are never
/// deleted. <see cref="RetainedRecordInterceptor"/> rejects every delete of a row whose type implements this, and
/// every update <see cref="RefuseChange"/> refuses, discovered from the change tracker as
/// <see cref="IImmutableRecord"/> is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The half of <see cref="IImmutableRecord"/> a balance can keep.</strong> A ledger row that is spent
/// down cannot be write-once, but it can still be a record that never disappears: what it granted, and what
/// became of it, stays readable however it ends. The first such row is <c>AccountCreditGrant</c> (AP.5.1), where
/// a claw-back is a recorded change to the grant rather than the grant's removal.
/// </para>
/// <para>
/// <strong>The same edge as <see cref="IImmutableRecord"/>, and stated for the same reason.</strong> This is an
/// application-path guarantee: <c>ExecuteDelete</c> and raw SQL go around it and are held closed by
/// <c>BulkOperationBoundaryTests</c>, and a database cascade goes around it by design. A documented erasure path
/// would be a deliberate exception, written once and reviewed — and would have to opt out of this interceptor
/// explicitly, because one that loads rows and removes them through EF is refused here.
/// </para>
/// <para>
/// <strong>Which updates are allowed is the entity's to say</strong>, because a check constraint sees one row at
/// a time and cannot compare it with what it was: a grant's credits raised from 500 to 5,000 with 500 left is a
/// row every check accepts. The comparison is against the values the change tracker loaded, so a row attached with
/// its new values already set has no "before" to compare with — the same tracked-entries edge as above.
/// </para>
/// </remarks>
public interface IRetainedRecord
{
    /// <summary>Why this change to a stored row is refused, or null to allow it.</summary>
    /// <param name="original">The value a property held when the row was loaded, by property name.</param>
    string? RefuseChange(Func<string, object?> original);
}
