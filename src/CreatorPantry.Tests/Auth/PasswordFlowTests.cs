using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Modules.Auth.Gateways;
using CreatorPantry.Domain.Managers.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Auth;

/// <summary>Password reset and change through the real seam, Identity token providers, and SQLite.</summary>
public sealed class PasswordFlowTests : IDisposable
{
    private const string Email = "cook@example.com";
    private const string OriginalPassword = "correct horse battery";
    private const string NewPassword = "a brand new passphrase";

    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Confirmed_account_receives_a_token_that_resets_the_password()
    {
        await _services.CreateConfirmedUserAsync(Email, OriginalPassword);

        var token = await RequestTokenAsync();
        var result = await CompleteAsync(Email, token, NewPassword);

        Assert.Equal(PasswordServiceModel.ResetCompleted, result.Value);
        Assert.Equal(AccountMessageKind.PasswordChanged, _services.Messages.Messages[0].Kind);
        Assert.True((await ChangeAsync(NewPassword, "yet another passphrase")).Succeeded); // new password works
    }

    [Fact]
    public async Task Token_is_single_use()
    {
        await _services.CreateConfirmedUserAsync(Email, OriginalPassword);
        var token = await RequestTokenAsync();
        await CompleteAsync(Email, token, NewPassword);

        var replay = await CompleteAsync(Email, token, "a third passphrase here");

        Assert.Equal(AuthErrorCodes.PasswordResetInvalidToken, replay.Error!.Code);
    }

    [Fact]
    public async Task Token_lifespan_is_one_hour()
    {
        await using var scope = _services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;

        Assert.Equal(TimeSpan.FromHours(1), options.TokenLifespan);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        // Identity's DataProtectorTokenProvider reads the system clock rather than TimeProvider, so expiry
        // is proven with a zero lifespan: the token is already past its lifespan when it is redeemed.
        using var services = new SqliteAuthServices(collection =>
            collection.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.Zero));
        await services.CreateConfirmedUserAsync(Email, OriginalPassword);
        await using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuthFacade>().RequestPasswordResetAsync(
                new RequestPasswordResetViewModel { Email = Email }, TestContext.Current.CancellationToken);
        }

        var token = services.Messages.Messages.Single().Token!;
        await using var completeScope = services.CreateScope();
        var result = await completeScope.ServiceProvider.GetRequiredService<IAuthFacade>().CompletePasswordResetAsync(
            new CompletePasswordResetViewModel { Email = Email, Token = token, NewPassword = NewPassword },
            TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.PasswordResetInvalidToken, result.Error!.Code);
    }

    [Theory]
    [InlineData("not base64url!")]
    [InlineData("dGFtcGVyZWQ")] // valid Base64Url, not a real token
    public async Task Unreadable_or_forged_tokens_share_the_invalid_token_error(string token)
    {
        await _services.CreateConfirmedUserAsync(Email, OriginalPassword);

        var result = await CompleteAsync(Email, token, NewPassword);

        Assert.Equal(AuthErrorCodes.PasswordResetInvalidToken, result.Error!.Code);
    }

    [Fact]
    public async Task Unknown_email_and_bad_token_are_indistinguishable()
    {
        await _services.CreateConfirmedUserAsync(Email, OriginalPassword);

        var known = await CompleteAsync(Email, "dGFtcGVyZWQ", NewPassword);
        var unknown = await CompleteAsync("nobody@example.com", "dGFtcGVyZWQ", NewPassword);

        Assert.Equal(AuthErrorCodes.PasswordResetInvalidToken, known.Error!.Code);
        Assert.Equal((known.Error.Code, known.Error.Message), (unknown.Error!.Code, unknown.Error.Message));
        Assert.Empty(known.Error.FieldErrors);
        Assert.Empty(unknown.Error.FieldErrors);
    }

    [Fact]
    public async Task Another_users_token_does_not_reset_this_account()
    {
        await _services.CreateConfirmedUserAsync(Email, OriginalPassword);
        await _services.CreateConfirmedUserAsync("other@example.com", OriginalPassword);
        var otherToken = await RequestTokenAsync("other@example.com");

        var result = await CompleteAsync(Email, otherToken, NewPassword);

        Assert.Equal(AuthErrorCodes.PasswordResetInvalidToken, result.Error!.Code);
    }

    [Fact]
    public async Task Unconfirmed_and_unknown_addresses_receive_no_message()
    {
        await RegisterUnconfirmedAsync(Email);

        var unconfirmed = await RequestAsync(Email);
        var unknown = await RequestAsync("nobody@example.com");

        Assert.Equal(PasswordServiceModel.ResetRequested, unconfirmed.Value);
        Assert.Equal(PasswordServiceModel.ResetRequested, unknown.Value);
        Assert.Empty(_services.Messages.Messages);
    }

    [Fact]
    public async Task Change_with_the_wrong_current_password_is_rejected()
    {
        await _services.CreateConfirmedUserAsync(Email, OriginalPassword);

        var result = await ChangeAsync("not the current password", NewPassword);

        Assert.Equal(AuthErrorCodes.PasswordChangeInvalid, result.Error!.Code);
        Assert.True(result.Error.FieldErrors.ContainsKey("currentPassword"));
        Assert.Empty(_services.Messages.Messages);
    }

    [Fact]
    public async Task Change_sends_a_notice_and_invalidates_outstanding_reset_tokens()
    {
        await _services.CreateConfirmedUserAsync(Email, OriginalPassword);
        var token = await RequestTokenAsync();

        var changed = await ChangeAsync(OriginalPassword, NewPassword);
        var reset = await CompleteAsync(Email, token, "a third passphrase here");

        Assert.Equal(PasswordServiceModel.Changed, changed.Value);
        Assert.Equal(AccountMessageKind.PasswordChanged, _services.Messages.Messages[0].Kind);
        Assert.Equal(AuthErrorCodes.PasswordResetInvalidToken, reset.Error!.Code);
    }

    [Fact]
    public async Task Tokens_and_emails_never_reach_logs_or_message_text()
    {
        await _services.CreateConfirmedUserAsync(Email, OriginalPassword);
        var token = await RequestTokenAsync();
        await CompleteAsync(Email, token, NewPassword);
        await RequestAsync("nobody@example.com");

        var message = _services.Messages.Messages.Last(m => m.Kind == AccountMessageKind.PasswordReset);
        Assert.DoesNotContain(token, message.ToString());
        Assert.DoesNotContain(Email, message.ToString());

        Assert.NotEmpty(_services.Logs.Entries);
        Assert.All(_services.Logs.Entries, entry =>
        {
            Assert.DoesNotContain(token, entry);
            Assert.DoesNotContain(Email, entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nobody@example.com", entry, StringComparison.OrdinalIgnoreCase);
        });
    }

    private async Task<string> RequestTokenAsync(string email = Email)
    {
        var result = await RequestAsync(email);
        Assert.True(result.Succeeded);

        var message = _services.Messages.Messages.First(m => m.Kind == AccountMessageKind.PasswordReset && m.RecipientEmail == email);
        return message.Token!;
    }

    private Task<OperationResult<PasswordServiceModel>> RequestAsync(string email) =>
        WithFacade(facade => facade.RequestPasswordResetAsync(new RequestPasswordResetViewModel { Email = email }, TestContext.Current.CancellationToken));

    private Task<OperationResult<PasswordServiceModel>> CompleteAsync(string email, string token, string newPassword) =>
        WithFacade(facade => facade.CompletePasswordResetAsync(
            new CompletePasswordResetViewModel { Email = email, Token = token, NewPassword = newPassword }, TestContext.Current.CancellationToken));

    private async Task<OperationResult<PasswordServiceModel>> ChangeAsync(string current, string next)
    {
        await using var scope = _services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<UserManager<Domain.Modules.Auth.Data.Entities.ApplicationUser>>()
            .FindByEmailAsync(Email);
        return await scope.ServiceProvider.GetRequiredService<IAuthFacade>().ChangePasswordAsync(
            account!.Id, new ChangePasswordViewModel { CurrentPassword = current, NewPassword = next }, TestContext.Current.CancellationToken);
    }

    private Task RegisterUnconfirmedAsync(string email) =>
        WithFacade(facade => facade.RegisterAsync(
            new RegisterUserViewModel { Email = email, Password = OriginalPassword, DisplayName = "Sam" }, TestContext.Current.CancellationToken));

    private async Task<T> WithFacade<T>(Func<IAuthFacade, Task<T>> action)
    {
        await using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAuthFacade>());
    }
}
