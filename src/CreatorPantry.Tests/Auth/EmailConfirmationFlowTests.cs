using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Modules.Auth.Gateways;
using CreatorPantry.Domain.Managers.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Auth;

/// <summary>Registration and email confirmation through the real seam, Identity token providers, and SQLite.</summary>
public sealed class EmailConfirmationFlowTests : IDisposable
{
    private const string Email = "cook@example.com";
    private const string Password = "correct horse battery";

    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Registration_sends_a_confirmation_token_that_confirms_the_account_and_allows_sign_in()
    {
        await RegisterAsync(Email);
        var token = await ConfirmationTokenAsync();

        var result = await ConfirmAsync(Email, token);

        Assert.Equal(EmailConfirmationServiceModel.Confirmed, result.Value);
        Assert.True((await SignInAsync()).Succeeded);
    }

    private Task<OperationResult<SessionUserServiceModel>> SignInAsync() =>
        WithFacade(facade => facade.VerifyCredentialsAsync(
            new VerifyCredentialsViewModel { Email = Email, Password = Password }, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Confirming_twice_is_idempotent()
    {
        await RegisterAsync(Email);
        var token = await ConfirmationTokenAsync();
        await ConfirmAsync(Email, token);

        var replay = await ConfirmAsync(Email, token);

        Assert.Equal(EmailConfirmationServiceModel.Confirmed, replay.Value);
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
        await using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuthFacade>().RegisterAsync(
                new RegisterUserViewModel { Email = Email, Password = Password, DisplayName = "Sam" }, TestContext.Current.CancellationToken);
        }

        var token = services.Messages.Messages.Single(m => m.Kind == AccountMessageKind.EmailConfirmation).Token!;
        await using var confirmScope = services.CreateScope();
        var result = await confirmScope.ServiceProvider.GetRequiredService<IAuthFacade>().ConfirmEmailAsync(
            new ConfirmEmailViewModel { Email = Email, Token = token }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.EmailConfirmationInvalidToken, result.Error!.Code);
    }

    [Theory]
    [InlineData("not base64url!")]
    [InlineData("dGFtcGVyZWQ")] // valid Base64Url, not a real token
    public async Task Unreadable_or_forged_tokens_share_the_invalid_token_error(string token)
    {
        await RegisterAsync(Email);

        var result = await ConfirmAsync(Email, token);

        Assert.Equal(AuthErrorCodes.EmailConfirmationInvalidToken, result.Error!.Code);
    }

    [Fact]
    public async Task Unknown_email_and_bad_token_are_indistinguishable()
    {
        await RegisterAsync(Email);

        var known = await ConfirmAsync(Email, "dGFtcGVyZWQ");
        var unknown = await ConfirmAsync("nobody@example.com", "dGFtcGVyZWQ");

        Assert.Equal(AuthErrorCodes.EmailConfirmationInvalidToken, known.Error!.Code);
        Assert.Equal((known.Error.Code, known.Error.Message), (unknown.Error!.Code, unknown.Error.Message));
        Assert.Empty(known.Error.FieldErrors);
        Assert.Empty(unknown.Error.FieldErrors);
    }

    [Fact]
    public async Task Another_users_token_does_not_confirm_this_account()
    {
        await RegisterAsync(Email);
        await RegisterAsync("other@example.com");
        var otherToken = await ConfirmationTokenAsync("other@example.com");

        var result = await ConfirmAsync(Email, otherToken);

        Assert.Equal(AuthErrorCodes.EmailConfirmationInvalidToken, result.Error!.Code);
    }

    [Fact]
    public async Task A_duplicate_registration_attempt_sends_no_second_confirmation()
    {
        await RegisterAsync(Email);
        var messagesAfterFirstRegistration = _services.Messages.Messages.Count;

        await RegisterAsync(Email);

        Assert.Equal(messagesAfterFirstRegistration, _services.Messages.Messages.Count);
    }

    [Fact]
    public async Task Tokens_and_emails_never_reach_logs_or_message_text()
    {
        await RegisterAsync(Email);
        var token = await ConfirmationTokenAsync();
        await ConfirmAsync(Email, token);

        var message = _services.Messages.Messages.Single(m => m.Kind == AccountMessageKind.EmailConfirmation);
        Assert.DoesNotContain(token, message.ToString());
        Assert.DoesNotContain(Email, message.ToString());

        Assert.NotEmpty(_services.Logs.Entries);
        Assert.All(_services.Logs.Entries, entry =>
        {
            Assert.DoesNotContain(token, entry);
            Assert.DoesNotContain(Email, entry, StringComparison.OrdinalIgnoreCase);
        });
    }

    private async Task<string> ConfirmationTokenAsync(string email = Email)
    {
        var message = _services.Messages.Messages.First(m => m.Kind == AccountMessageKind.EmailConfirmation && m.RecipientEmail == email);
        return message.Token!;
    }

    private Task RegisterAsync(string email) =>
        WithFacade(facade => facade.RegisterAsync(
            new RegisterUserViewModel { Email = email, Password = Password, DisplayName = "Sam" }, TestContext.Current.CancellationToken));

    private Task<OperationResult<EmailConfirmationServiceModel>> ConfirmAsync(string email, string token) =>
        WithFacade(facade => facade.ConfirmEmailAsync(
            new ConfirmEmailViewModel { Email = email, Token = token }, TestContext.Current.CancellationToken));

    private async Task<T> WithFacade<T>(Func<IAuthFacade, Task<T>> action)
    {
        await using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAuthFacade>());
    }
}
