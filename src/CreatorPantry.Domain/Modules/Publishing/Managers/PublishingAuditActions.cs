namespace CreatorPantry.Domain.Modules.Publishing.Managers;

/// <summary>The audited publishing actions (auth.md: publishing is a sensitive operation).</summary>
public static class PublishingAuditActions
{
    public const string ResourceType = "publishing_record";

    /// <summary>
    /// A creator confirmed a publication. Written in the same transaction as the pending ledger row, so the
    /// confirmation and the record of who made it commit together or not at all.
    /// </summary>
    public const string Confirmed = "publishing.record.confirmed";

    /// <summary>
    /// How the provider answered the submit, recorded after the call as a second entry.
    /// </summary>
    /// <remarks>
    /// A separate entry rather than an edit of the first: the confirmation happened whatever the provider then
    /// said, and an audit trail that rewrote itself to match the outcome would lose the attempt.
    /// </remarks>
    public const string Submitted = "publishing.record.submitted";

    /// <summary>A creator asked for a publication to be cancelled (PUB-003).</summary>
    /// <remarks>
    /// Written whether or not the provider has to be asked, because the asking is the act being audited. What
    /// came of it is the entry below.
    /// </remarks>
    public const string CancellationRequested = "publishing.record.cancellation_requested";

    /// <summary>How the cancellation ended, recorded after the provider answered — or instead of asking it.</summary>
    public const string Cancelled = "publishing.record.cancelled";

    /// <summary>
    /// What reconciliation made of a record the provider had not accounted for (13.10c).
    /// </summary>
    /// <remarks>
    /// Written with no actor: it runs as the service, long after whoever confirmed the publication, and naming
    /// them would say a person did this.
    /// </remarks>
    public const string Reconciled = "publishing.record.reconciled";
}
