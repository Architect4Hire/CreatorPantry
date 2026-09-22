namespace CreatorPantry.Domain.Models.DomainModels.Auth;

/// <summary>The data needed to create a user; Identity-specific fields are set by the repository.</summary>
public sealed record NewUser(string Email, string DisplayName, DateTimeOffset CreatedAt);

public enum UserCreationStatus
{
    Created,

    /// <summary>An account already uses this email.</summary>
    Duplicate,

    /// <summary>The password failed Identity's password validators.</summary>
    InvalidPassword,

    /// <summary>The email failed Identity's user validators.</summary>
    InvalidEmail,
}

/// <param name="Errors">Human-readable validation errors for <see cref="UserCreationStatus.InvalidPassword"/> or <see cref="UserCreationStatus.InvalidEmail"/>.</param>
public sealed record UserCreationResult(UserCreationStatus Status, string? UserId, IReadOnlyList<string> Errors)
{
    public static UserCreationResult Created(string userId) => new(UserCreationStatus.Created, userId, []);

    public static UserCreationResult Duplicate { get; } = new(UserCreationStatus.Duplicate, null, []);

    public static UserCreationResult Invalid(UserCreationStatus status, IReadOnlyList<string> errors) => new(status, null, errors);
}
