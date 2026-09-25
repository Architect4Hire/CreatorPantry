using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.ServiceDefaults;
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

// The provider-specific classifier first: AddAiModule's fallback cannot read a provider SDK's status code, so
// it cannot tell a rate limit or a safety block from a generic transient fault. AddAiModule also validates and
// loads every prompt template, so a malformed one stops this host rather than a job.
builder.Services.AddSingleton<IAiFailureClassifier, AzureInferenceFailureClassifier>();
builder.AddAiResilience(static exception => exception is AiTransientFailureException);
builder.Services.AddAiModule(builder.Configuration, AiResilience.PipelineKey);

builder.Services.AddOutbox();
builder.Services.AddHostedService<OutboxDispatcherHostedService>();

var host = builder.Build();
host.Run();
