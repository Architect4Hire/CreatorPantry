namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// Marks an entity as write-once. <see cref="ImmutableRecordInterceptor"/> rejects every update and delete
/// of a row whose type implements this, discovered from the change tracker rather than from a list — so a
/// new immutable entity is covered the moment it implements the interface and nothing has to remember it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The guarantee has a precise edge, and overstating it would be worse than not having it.</strong>
/// The interceptor runs on <c>SaveChanges</c> and sees only tracked entries. Three paths go around it:
/// <c>ExecuteUpdate</c> and <c>ExecuteDelete</c>, which never populate the change tracker and never call
/// <c>SaveChanges</c>; raw SQL through <c>ExecuteSql</c>; and a database-level cascade, such as deleting a
/// workspace, which removes dependent rows without EF materialising them. The first two are held closed by
/// <c>BulkOperationBoundaryTests</c>, which fails the build if either appears in domain code; the third is
/// deliberate, because erasing a workspace must be able to erase its history.
/// </para>
/// <para>
/// So this is an application-path guarantee: no feature code edits or deletes one of these rows. It is not a
/// claim that the row is physically indelible.
/// </para>
/// <para>
/// This is a marker, not a lifecycle. It says "an existing row of this type is never edited or removed by
/// ordinary application code"; it does not describe retention, erasure, or archival. A documented erasure
/// path — the model tenancy.md's <c>IgnoreQueryFilters</c> carve-out follows — would be a deliberate
/// exception to this rule, written once and reviewed, not a reason to relax it. Note that such a path will
/// have to opt out of this interceptor explicitly: an erasure that loads rows and removes them through EF is
/// refused here, by design.
/// </para>
/// <para>
/// <see cref="CreatorPantry.Domain.Managers.Audit.AuditLog"/> is deliberately not migrated onto this marker
/// here. It predates the interface and has its own equivalent interceptor with its own message; folding the
/// two together is a worthwhile cleanup and a change to shipped behaviour, so it belongs in its own commit
/// rather than riding along with the recipe version model.
/// </para>
/// </remarks>
public interface IImmutableRecord;
