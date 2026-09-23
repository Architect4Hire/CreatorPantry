namespace CreatorPantry.Domain.Managers.Outbox;

/// <summary>Lifecycle of one <c>OutboxMessage</c>. Ordered, not a bitmask.</summary>
public enum OutboxMessageStatus
{
    /// <summary>Waiting for <see cref="Data.OutboxMessage.AvailableAt"/>, or waiting again after a failed attempt.</summary>
    Pending = 0,

    /// <summary>Claimed by a dispatcher; <see cref="Data.OutboxMessage.LeaseExpiresAt"/> reclaims it if the lease lapses.</summary>
    Leased = 10,

    /// <summary>The handler ran successfully. Terminal.</summary>
    Completed = 20,

    /// <summary>Every attempt failed; retries are exhausted. Terminal until a documented recovery path handles it.</summary>
    Poisoned = 30,
}
