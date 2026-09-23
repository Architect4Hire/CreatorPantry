using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Managers;
using System.Buffers.Text;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Auth.Data;

internal sealed class UserRepository(UserManager<ApplicationUser> userManager, ILogger<UserRepository> logger)
    : IUserRepository
{
    private static readonly string[] DuplicateCodes = ["DuplicateUserName", "DuplicateEmail"];

    public async Task<UserCreationResult> CreateAsync(NewUser user, string password, CancellationToken cancellationToken)
    {
        // UserManager does not accept a cancellation token; stop before starting the write instead.
        cancellationToken.ThrowIfCancellationRequested();

        var entity = new ApplicationUser
        {
            UserName = user.Email,
            Email = user.Email,
            DisplayName = user.DisplayName,
            CreatedAt = user.CreatedAt,
        };

        IdentityResult result;
        try
        {
            // Identity validates the password before the user, so a duplicate email is only reported
            // for passwords that would otherwise be accepted.
            result = await userManager.CreateAsync(entity, password);
        }
        catch (DbUpdateException)
        {
            // A concurrent registration may have won the unique index on the normalized user name.
            if (await userManager.FindByEmailAsync(user.Email) is null)
            {
                throw;
            }

            logger.LogInformation("Registration lost a race with an existing account.");
            return UserCreationResult.Duplicate;
        }

        if (result.Succeeded)
        {
            logger.LogInformation("Created user {UserId}.", entity.Id);
            return UserCreationResult.Created(entity.Id);
        }

        if (result.Errors.Any(error => DuplicateCodes.Contains(error.Code)))
        {
            logger.LogInformation("Registration requested for an existing account.");
            return UserCreationResult.Duplicate;
        }

        var passwordErrors = PasswordErrors(result);
        return passwordErrors.Count > 0
            ? UserCreationResult.Invalid(UserCreationStatus.InvalidPassword, passwordErrors)
            : UserCreationResult.Invalid(UserCreationStatus.InvalidEmail, result.Errors.Select(e => e.Description).ToList());
    }

    public async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ToAccount(await userManager.FindByEmailAsync(email));
    }

    public async Task<UserAccount?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ToAccount(await userManager.FindByIdAsync(userId));
    }

    public async Task<string> GeneratePasswordResetTokenAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var token = await userManager.GeneratePasswordResetTokenAsync(user);

        // Identity tokens are standard Base64; transport them URL-safe.
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
    }

    public async Task<PasswordUpdateResult> ResetPasswordAsync(
        string userId, string encodedToken, string newPassword, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return PasswordUpdateResult.Of(PasswordUpdateStatus.NotFound);
        }

        if (!TryDecodeToken(encodedToken, out var token))
        {
            return PasswordUpdateResult.Of(PasswordUpdateStatus.InvalidToken);
        }

        // Identity verifies the token before validating the new password. Success rotates the security
        // stamp, which invalidates this and every other outstanding token.
        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (result.Succeeded)
        {
            logger.LogInformation("Password reset for user {UserId}.", user.Id);
            return PasswordUpdateResult.Of(PasswordUpdateStatus.Succeeded);
        }

        var passwordErrors = PasswordErrors(result);
        if (passwordErrors.Count > 0)
        {
            return PasswordUpdateResult.InvalidPassword(passwordErrors);
        }

        logger.LogInformation("Password reset rejected for user {UserId}: {ErrorCodes}.",
            user.Id, string.Join(",", result.Errors.Select(error => error.Code)));
        return PasswordUpdateResult.Of(PasswordUpdateStatus.InvalidToken);
    }

    public async Task<PasswordUpdateResult> ChangePasswordAsync(
        string userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return PasswordUpdateResult.Of(PasswordUpdateStatus.NotFound);
        }

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded)
        {
            logger.LogInformation("Password changed for user {UserId}.", user.Id);
            return PasswordUpdateResult.Of(PasswordUpdateStatus.Succeeded);
        }

        if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
        {
            return PasswordUpdateResult.Of(PasswordUpdateStatus.IncorrectCurrentPassword);
        }

        var passwordErrors = PasswordErrors(result);
        if (passwordErrors.Count > 0)
        {
            return PasswordUpdateResult.InvalidPassword(passwordErrors);
        }

        throw new InvalidOperationException(
            $"Password change failed: {string.Join(",", result.Errors.Select(error => error.Code))}.");
    }

    public async Task<CredentialCheckResult> CheckCredentialsAsync(string email, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            // Spend a hash computation so an unknown email takes as long as a wrong password.
            userManager.PasswordHasher.HashPassword(new ApplicationUser(), password);
            return CredentialCheckResult.Of(CredentialCheckStatus.Invalid);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            // Same cost as a real check, with no lockout side effects.
            userManager.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash ?? string.Empty, password);
            logger.LogInformation("Sign-in rejected for locked-out user {UserId}.", user.Id);
            return CredentialCheckResult.Of(CredentialCheckStatus.LockedOut);
        }

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            await userManager.AccessFailedAsync(user);
            logger.LogInformation("Failed sign-in for user {UserId}.", user.Id);
            return CredentialCheckResult.Of(CredentialCheckStatus.Invalid);
        }

        if (await userManager.GetAccessFailedCountAsync(user) > 0)
        {
            await userManager.ResetAccessFailedCountAsync(user);
        }

        if (!user.EmailConfirmed)
        {
            return CredentialCheckResult.Of(CredentialCheckStatus.EmailUnconfirmed);
        }

        logger.LogInformation("Verified credentials for user {UserId}.", user.Id);
        return new CredentialCheckResult(CredentialCheckStatus.Succeeded, await ToSessionUserAsync(user));
    }

    public async Task<SessionUser?> GetSessionUserAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByIdAsync(userId);
        return user is null ? null : await ToSessionUserAsync(user);
    }

    private async Task<SessionUser> ToSessionUserAsync(ApplicationUser user) =>
        new(user.Id, user.DisplayName, [.. await userManager.GetRolesAsync(user)], await userManager.GetSecurityStampAsync(user),
            CanSignIn: user.EmailConfirmed && !await userManager.IsLockedOutAsync(user));

    private async Task<ApplicationUser> RequireUserAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await userManager.FindByIdAsync(userId)
            ?? throw new InvalidOperationException($"User {userId} does not exist.");
    }

    private static bool TryDecodeToken(string encodedToken, out string token)
    {
        token = string.Empty;
        if (!Base64Url.IsValid(encodedToken))
        {
            return false;
        }

        token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encodedToken));
        return true;
    }

    private static List<string> PasswordErrors(IdentityResult result) =>
        result.Errors
            .Where(error => error.Code.StartsWith("Password", StringComparison.Ordinal)
                && error.Code != nameof(IdentityErrorDescriber.PasswordMismatch))
            .Select(error => error.Description)
            .ToList();

    private static UserAccount? ToAccount(ApplicationUser? user) =>
        user is null ? null : new UserAccount(user.Id, user.Email!, user.EmailConfirmed);
}
