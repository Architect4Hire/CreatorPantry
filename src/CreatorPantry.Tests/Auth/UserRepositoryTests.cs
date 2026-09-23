using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data;
using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Auth.Managers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Auth;

public sealed class UserRepositoryTests : IDisposable
{
    private const string StrongPassword = "correct horse battery";

    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Creates_an_unconfirmed_user_with_email_as_user_name()
    {
        var result = await CreateAsync(new NewUser("cook@example.com", "Sam", SqliteAuthServices.Now), StrongPassword);

        Assert.Equal(UserCreationStatus.Created, result.Status);

        await using var scope = _services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(result.UserId!);
        Assert.NotNull(user);
        Assert.Equal("cook@example.com", user.UserName);
        Assert.Equal("cook@example.com", user.Email);
        Assert.Equal("Sam", user.DisplayName);
        Assert.Equal(SqliteAuthServices.Now, user.CreatedAt);
        Assert.Null(user.LastWorkspaceId);
        Assert.False(user.EmailConfirmed);
        Assert.NotEqual(StrongPassword, user.PasswordHash);
    }

    [Theory]
    [InlineData("cook@example.com")]
    [InlineData("COOK@Example.com")] // Identity compares normalized emails
    public async Task Second_registration_for_the_same_email_is_a_duplicate(string secondEmail)
    {
        await CreateAsync(new NewUser("cook@example.com", "Sam", SqliteAuthServices.Now), StrongPassword);

        var result = await CreateAsync(new NewUser(secondEmail, "Other", SqliteAuthServices.Now), StrongPassword);

        Assert.Equal(UserCreationStatus.Duplicate, result.Status);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task Weak_password_is_reported_before_the_duplicate_check()
    {
        // Anti-enumeration: an existing email must not change the outcome for an invalid password.
        await CreateAsync(new NewUser("cook@example.com", "Sam", SqliteAuthServices.Now), StrongPassword);

        var existing = await CreateAsync(new NewUser("cook@example.com", "Sam", SqliteAuthServices.Now), "short");
        var fresh = await CreateAsync(new NewUser("new@example.com", "Sam", SqliteAuthServices.Now), "short");

        Assert.Equal(UserCreationStatus.InvalidPassword, existing.Status);
        Assert.Equal(fresh.Status, existing.Status);
        Assert.Equal(fresh.Errors, existing.Errors);
    }

    [Fact]
    public async Task Email_rejected_by_identity_is_reported_as_invalid_email()
    {
        var result = await CreateAsync(new NewUser("not-an-email", "Sam", SqliteAuthServices.Now), StrongPassword);

        Assert.Equal(UserCreationStatus.InvalidEmail, result.Status);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Email_with_an_apostrophe_is_accepted_as_a_user_name()
    {
        var result = await CreateAsync(new NewUser("o'brien@example.com", "Pat", SqliteAuthServices.Now), StrongPassword);

        Assert.Equal(UserCreationStatus.Created, result.Status);
    }

    [Fact]
    public async Task Cancelled_request_does_not_create_a_user()
    {
        await using var scope = _services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => repository.CreateAsync(
            new NewUser("cook@example.com", "Sam", SqliteAuthServices.Now), StrongPassword, new CancellationToken(canceled: true)));

        Assert.Equal(UserCreationStatus.Created,
            (await CreateAsync(new NewUser("cook@example.com", "Sam", SqliteAuthServices.Now), StrongPassword)).Status);
    }

    private async Task<UserCreationResult> CreateAsync(NewUser user, string password)
    {
        await using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .CreateAsync(user, password, TestContext.Current.CancellationToken);
    }
}
