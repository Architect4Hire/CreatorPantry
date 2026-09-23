using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Auth.Business;
using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Modules.Auth.Gateways;
using CreatorPantry.Domain.Managers.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Auth;

public class AuthDataLayerTests
{
    private static readonly UserAccount Account = new("u1", "cook@example.com", EmailConfirmed: true);

    private readonly FakeUserRepository _repository = new();
    private readonly InMemoryAccountMessageSink _sink = new();

    [Fact]
    public async Task Registration_forwards_the_user_password_and_cancellation_token()
    {
        using var cts = new CancellationTokenSource();
        var user = new NewUser("cook@example.com", "Sam", SqliteAuthServices.Now);

        var result = await CreateDataLayer().RegisterUserAsync(user, "pw", cts.Token);

        Assert.Equal(UserCreationStatus.Created, result.Status);
        Assert.Equal((user, "pw", cts.Token), _repository.Creates.Single());
    }

    [Fact]
    public async Task Issuing_a_reset_sends_the_repository_token_with_its_expiry()
    {
        var expiresAt = SqliteAuthServices.Now.AddHours(1);

        await CreateDataLayer().IssuePasswordResetAsync(Account, SqliteAuthServices.Now, expiresAt, TestContext.Current.CancellationToken);

        var message = Assert.Single(_sink.Messages);
        Assert.Equal(AccountMessageKind.PasswordReset, message.Kind);
        Assert.Equal(("u1", "cook@example.com"), (message.RecipientUserId, message.RecipientEmail));
        Assert.Equal(_repository.ResetToken, message.Token);
        Assert.Equal(expiresAt, message.ExpiresAt);
    }

    [Fact]
    public async Task Changed_notice_carries_no_token()
    {
        await CreateDataLayer().SendPasswordChangedNoticeAsync(Account, SqliteAuthServices.Now, TestContext.Current.CancellationToken);

        var message = Assert.Single(_sink.Messages);
        Assert.Equal(AccountMessageKind.PasswordChanged, message.Kind);
        Assert.Null(message.Token);
    }

    [Fact]
    public async Task Issuing_a_confirmation_sends_the_repository_token_with_its_expiry()
    {
        var expiresAt = SqliteAuthServices.Now.AddHours(1);

        await CreateDataLayer().IssueEmailConfirmationAsync(Account, SqliteAuthServices.Now, expiresAt, TestContext.Current.CancellationToken);

        var message = Assert.Single(_sink.Messages);
        Assert.Equal(AccountMessageKind.EmailConfirmation, message.Kind);
        Assert.Equal(("u1", "cook@example.com"), (message.RecipientUserId, message.RecipientEmail));
        Assert.Equal(_repository.ResetToken, message.Token);
        Assert.Equal(expiresAt, message.ExpiresAt);
    }

    [Fact]
    public async Task Confirm_email_delegates_to_the_repository_by_user_id()
    {
        _repository.ConfirmResult = EmailConfirmationResult.Of(EmailConfirmationStatus.Succeeded);

        var result = await CreateDataLayer().ConfirmEmailAsync(Account, "encoded-token", TestContext.Current.CancellationToken);

        Assert.Equal(EmailConfirmationStatus.Succeeded, result.Status);
        Assert.Equal(("u1", "encoded-token"), _repository.ConfirmCalls.Single());
    }

    private AuthDataLayer CreateDataLayer() => new(_repository, _sink);
}

public class AuthBusinessTests
{
    private static readonly UserAccount Confirmed = new("u1", "cook@example.com", EmailConfirmed: true);

    private readonly FakeAuthDataLayer _dataLayer = new();

    [Fact]
    public async Task Registration_builds_the_new_user_from_trimmed_input_and_the_clock()
    {
        var model = new RegisterUserViewModel { Email = "  cook@example.com ", Password = " pass phrase! ", DisplayName = " Sam " };

        await CreateBusiness().RegisterAsync(model, TestContext.Current.CancellationToken);

        var (user, password) = _dataLayer.Registrations.Single();
        Assert.Equal(new NewUser("cook@example.com", "Sam", SqliteAuthServices.Now), user);
        Assert.Equal(" pass phrase! ", password); // passwords are never trimmed
    }

    [Fact]
    public async Task Registration_created_and_duplicate_produce_identical_results()
    {
        _dataLayer.CreateResult = UserCreationResult.Created("u1");
        var created = await CreateBusiness().RegisterAsync(RegisterUserViewModelValidatorTests.Valid(), TestContext.Current.CancellationToken);
        _dataLayer.CreateResult = UserCreationResult.Duplicate;
        var duplicate = await CreateBusiness().RegisterAsync(RegisterUserViewModelValidatorTests.Valid(), TestContext.Current.CancellationToken);

        Assert.True(created.Succeeded);
        Assert.Equal(created.Value, duplicate.Value);
        Assert.Equal(RegistrationServiceModel.PendingConfirmation, created.Value);
    }

    [Fact]
    public async Task Registration_of_a_new_account_issues_a_one_hour_confirmation_token()
    {
        _dataLayer.CreateResult = UserCreationResult.Created("u1");

        await CreateBusiness().RegisterAsync(RegisterUserViewModelValidatorTests.Valid(), TestContext.Current.CancellationToken);

        var issued = Assert.Single(_dataLayer.IssuedConfirmations);
        Assert.Equal(new UserAccount("u1", "cook@example.com", EmailConfirmed: false), issued.Account);
        Assert.Equal(SqliteAuthServices.Now.AddHours(1), issued.ExpiresAt);
    }

    [Fact]
    public async Task Registration_of_a_duplicate_account_issues_no_confirmation()
    {
        _dataLayer.CreateResult = UserCreationResult.Duplicate;

        await CreateBusiness().RegisterAsync(RegisterUserViewModelValidatorTests.Valid(), TestContext.Current.CancellationToken);

        Assert.Empty(_dataLayer.IssuedConfirmations);
    }

    [Theory]
    [InlineData(UserCreationStatus.InvalidPassword, "password")]
    [InlineData(UserCreationStatus.InvalidEmail, "email")]
    public async Task Registration_identity_failures_become_field_errors(UserCreationStatus status, string field)
    {
        _dataLayer.CreateResult = UserCreationResult.Invalid(status, ["Problem."]);

        var result = await CreateBusiness().RegisterAsync(RegisterUserViewModelValidatorTests.Valid(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.RegistrationInvalid, result.Error!.Code);
        Assert.Equal(["Problem."], result.Error.FieldErrors[field]);
    }

    [Fact]
    public async Task Reset_request_for_a_confirmed_account_issues_a_one_hour_token()
    {
        _dataLayer.Account = Confirmed;

        var result = await CreateBusiness().RequestPasswordResetAsync(ResetRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(PasswordServiceModel.ResetRequested, result.Value);
        var issued = Assert.Single(_dataLayer.IssuedResets);
        Assert.Equal(Confirmed, issued.Account);
        Assert.Equal(SqliteAuthServices.Now.AddHours(1), issued.ExpiresAt);
    }

    [Fact]
    public async Task Reset_request_for_unknown_or_unconfirmed_accounts_sends_nothing_but_responds_identically()
    {
        _dataLayer.Account = Confirmed;
        var known = await CreateBusiness().RequestPasswordResetAsync(ResetRequest(), TestContext.Current.CancellationToken);
        _dataLayer.IssuedResets.Clear();

        _dataLayer.Account = null;
        var unknown = await CreateBusiness().RequestPasswordResetAsync(ResetRequest(), TestContext.Current.CancellationToken);
        _dataLayer.Account = Confirmed with { EmailConfirmed = false };
        var unconfirmed = await CreateBusiness().RequestPasswordResetAsync(ResetRequest(), TestContext.Current.CancellationToken);

        Assert.Empty(_dataLayer.IssuedResets);
        Assert.Equal(known.Value, unknown.Value);
        Assert.Equal(known.Value, unconfirmed.Value);
    }

    [Fact]
    public async Task Reset_completion_for_an_unknown_email_is_an_invalid_token_without_touching_passwords()
    {
        _dataLayer.Account = null;

        var result = await CreateBusiness().CompletePasswordResetAsync(CompleteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.PasswordResetInvalidToken, result.Error!.Code);
        Assert.Equal(0, _dataLayer.ResetCalls);
    }

    [Theory]
    [InlineData(PasswordUpdateStatus.InvalidToken)]
    [InlineData(PasswordUpdateStatus.NotFound)]
    public async Task Reset_completion_token_failures_share_one_error(PasswordUpdateStatus status)
    {
        _dataLayer.Account = Confirmed;
        _dataLayer.UpdateResult = PasswordUpdateResult.Of(status);

        var result = await CreateBusiness().CompletePasswordResetAsync(CompleteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.PasswordResetInvalidToken, result.Error!.Code);
        Assert.Empty(result.Error.FieldErrors);
        Assert.Empty(_dataLayer.ChangedNotices);
    }

    [Fact]
    public async Task Reset_completion_success_sends_a_changed_notice()
    {
        _dataLayer.Account = Confirmed;

        var result = await CreateBusiness().CompletePasswordResetAsync(CompleteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(PasswordServiceModel.ResetCompleted, result.Value);
        Assert.Equal([Confirmed], _dataLayer.ChangedNotices);
    }

    [Fact]
    public async Task Confirm_email_for_an_unknown_email_is_an_invalid_token_without_touching_identity()
    {
        _dataLayer.Account = null;

        var result = await CreateBusiness().ConfirmEmailAsync(ConfirmRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.EmailConfirmationInvalidToken, result.Error!.Code);
        Assert.Equal(0, _dataLayer.ConfirmEmailCalls);
    }

    [Theory]
    [InlineData(EmailConfirmationStatus.InvalidToken)]
    [InlineData(EmailConfirmationStatus.NotFound)]
    public async Task Confirm_email_token_failures_share_one_error(EmailConfirmationStatus status)
    {
        _dataLayer.Account = Confirmed;
        _dataLayer.ConfirmationResult = EmailConfirmationResult.Of(status);

        var result = await CreateBusiness().ConfirmEmailAsync(ConfirmRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.EmailConfirmationInvalidToken, result.Error!.Code);
        Assert.Empty(result.Error.FieldErrors);
    }

    [Fact]
    public async Task Confirm_email_success()
    {
        _dataLayer.Account = Confirmed;
        _dataLayer.ConfirmationResult = EmailConfirmationResult.Of(EmailConfirmationStatus.Succeeded);

        var result = await CreateBusiness().ConfirmEmailAsync(ConfirmRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(EmailConfirmationServiceModel.Confirmed, result.Value);
    }

    [Fact]
    public async Task Change_with_the_wrong_current_password_is_a_current_password_field_error()
    {
        _dataLayer.UpdateResult = PasswordUpdateResult.Of(PasswordUpdateStatus.IncorrectCurrentPassword);

        var result = await CreateBusiness().ChangePasswordAsync("u1", ChangeRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.PasswordChangeInvalid, result.Error!.Code);
        Assert.True(result.Error.FieldErrors.ContainsKey("currentPassword"));
        Assert.Empty(_dataLayer.ChangedNotices);
    }

    [Fact]
    public async Task Change_success_sends_a_changed_notice()
    {
        _dataLayer.Account = Confirmed;

        var result = await CreateBusiness().ChangePasswordAsync("u1", ChangeRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(PasswordServiceModel.Changed, result.Value);
        Assert.Equal([Confirmed], _dataLayer.ChangedNotices);
    }

    [Fact]
    public async Task Change_for_a_missing_account_reports_account_not_found()
    {
        _dataLayer.UpdateResult = PasswordUpdateResult.Of(PasswordUpdateStatus.NotFound);

        var result = await CreateBusiness().ChangePasswordAsync("gone", ChangeRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.AccountNotFound, result.Error!.Code);
    }

    internal static RequestPasswordResetViewModel ResetRequest() => new() { Email = "cook@example.com" };

    internal static CompletePasswordResetViewModel CompleteRequest() =>
        new() { Email = "cook@example.com", Token = "encoded-token", NewPassword = "a brand new passphrase" };

    internal static ChangePasswordViewModel ChangeRequest() =>
        new() { CurrentPassword = "correct horse battery", NewPassword = "a brand new passphrase" };

    internal static ConfirmEmailViewModel ConfirmRequest() =>
        new() { Email = "cook@example.com", Token = "encoded-token" };

    private AuthBusiness CreateBusiness() => new(_dataLayer, new FixedClock(), NullLogger<AuthBusiness>.Instance);
}

public class AuthFacadeTests
{
    private readonly FakeAuthBusiness _business = new();

    [Fact]
    public async Task Invalid_registration_returns_field_errors_without_calling_business()
    {
        var result = await CreateFacade().RegisterAsync(
            new RegisterUserViewModel { Email = "bad", Password = "short", DisplayName = "" }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.RegistrationInvalid, result.Error!.Code);
        Assert.Equal(["displayName", "email", "password"], result.Error.FieldErrors.Keys.Order());
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Invalid_reset_request_is_rejected_before_any_account_lookup()
    {
        var result = await CreateFacade().RequestPasswordResetAsync(new RequestPasswordResetViewModel { Email = "bad" },
            TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.PasswordResetInvalid, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Invalid_reset_completion_is_rejected_before_any_account_lookup()
    {
        var result = await CreateFacade().CompletePasswordResetAsync(
            new CompletePasswordResetViewModel { Email = "cook@example.com", Token = "", NewPassword = "short" },
            TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.PasswordResetInvalid, result.Error!.Code);
        Assert.Equal(["newPassword", "token"], result.Error.FieldErrors.Keys.Order());
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Invalid_confirm_email_is_rejected_before_any_account_lookup()
    {
        var result = await CreateFacade().ConfirmEmailAsync(
            new ConfirmEmailViewModel { Email = "bad", Token = "" }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.EmailConfirmationInvalid, result.Error!.Code);
        Assert.Equal(["email", "token"], result.Error.FieldErrors.Keys.Order());
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Invalid_change_is_rejected_without_calling_business()
    {
        var result = await CreateFacade().ChangePasswordAsync("u1",
            new ChangePasswordViewModel { CurrentPassword = "", NewPassword = "short" }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthErrorCodes.PasswordChangeInvalid, result.Error!.Code);
        Assert.Equal(["currentPassword", "newPassword"], result.Error.FieldErrors.Keys.Order());
        Assert.Equal(0, _business.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Change_requires_an_authenticated_user_id(string userId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateFacade().ChangePasswordAsync(userId, AuthBusinessTests.ChangeRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Valid_input_calls_business_once_per_operation()
    {
        var facade = CreateFacade();
        var ct = TestContext.Current.CancellationToken;

        await facade.RegisterAsync(RegisterUserViewModelValidatorTests.Valid(), ct);
        await facade.RequestPasswordResetAsync(AuthBusinessTests.ResetRequest(), ct);
        await facade.CompletePasswordResetAsync(AuthBusinessTests.CompleteRequest(), ct);
        await facade.ConfirmEmailAsync(AuthBusinessTests.ConfirmRequest(), ct);
        await facade.ChangePasswordAsync("u1", AuthBusinessTests.ChangeRequest(), ct);
        await facade.VerifyCredentialsAsync(new VerifyCredentialsViewModel { Email = "cook@example.com", Password = "pw" }, ct);
        await facade.ValidateSessionAsync(new ValidateSessionViewModel { UserId = "u1", SecurityStamp = "stamp" }, ct);

        Assert.Equal(7, _business.Calls);
    }

    [Fact]
    public async Task Malformed_sign_in_and_session_requests_never_reach_business()
    {
        var ct = TestContext.Current.CancellationToken;

        var signIn = await CreateFacade().VerifyCredentialsAsync(new VerifyCredentialsViewModel(), ct);
        var session = await CreateFacade().ValidateSessionAsync(new ValidateSessionViewModel(), ct);

        Assert.Equal(AuthErrorCodes.SignInInvalidRequest, signIn.Error!.Code);
        Assert.Equal(AuthErrorCodes.SessionInvalid, session.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    private AuthFacade CreateFacade() => new(
        new RegisterUserViewModelValidator(),
        new RequestPasswordResetViewModelValidator(),
        new CompletePasswordResetViewModelValidator(),
        new ChangePasswordViewModelValidator(),
        new ConfirmEmailViewModelValidator(),
        new VerifyCredentialsViewModelValidator(),
        new ValidateSessionViewModelValidator(),
        _business);
}

/// <summary>The real facade → business → data layer → repository → UserManager seam over SQLite.</summary>
public sealed class RegistrationSeamTests : IDisposable
{
    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task New_and_existing_emails_get_identical_responses()
    {
        var first = await RegisterAsync(RegisterUserViewModelValidatorTests.Valid());
        var second = await RegisterAsync(RegisterUserViewModelValidatorTests.Valid());

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public async Task Existing_and_new_emails_get_identical_errors_for_the_same_bad_input()
    {
        await RegisterAsync(RegisterUserViewModelValidatorTests.Valid());

        var existing = RegisterUserViewModelValidatorTests.Valid();
        existing.Password = "short";
        var fresh = RegisterUserViewModelValidatorTests.Valid();
        fresh.Email = "new@example.com";
        fresh.Password = "short";

        var existingResult = await RegisterAsync(existing);
        var freshResult = await RegisterAsync(fresh);

        Assert.Equal(freshResult.Error!.Code, existingResult.Error!.Code);
        Assert.Equal(freshResult.Error.FieldErrors, existingResult.Error.FieldErrors);
    }

    private async Task<OperationResult<RegistrationServiceModel>> RegisterAsync(RegisterUserViewModel model)
    {
        await using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAuthFacade>()
            .RegisterAsync(model, TestContext.Current.CancellationToken);
    }
}
