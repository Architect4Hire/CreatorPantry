using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApplicationTime();

// Not AddSqlServerDbContext: see ApiService/Program.cs for why (a pooled context cannot take the scoped
// IWorkspaceContext dependency CreatorPantryDbContext's constructor accepts).
builder.Services.AddDbContext<CreatorPantryDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString(CreatorPantryDbContext.ConnectionName)));
builder.EnrichSqlServerDbContext<CreatorPantryDbContext>();

// The worker gets the same model abstractions as the API, because generation, embedding and media jobs run
// here. See AiProviderRegistration for the unconfigured-development fallback.
builder.AddCreatorPantryAi();

// Validates and loads every prompt template now, so a malformed one stops this host rather than a job.
builder.Services.AddPromptTemplates();

builder.Services.AddOutbox();
builder.Services.AddHostedService<OutboxDispatcherHostedService>();

var host = builder.Build();
host.Run();
