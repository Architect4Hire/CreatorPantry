using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Outbox;
using CreatorPantry.Domain.Time;
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

builder.Services.AddOutbox();
builder.Services.AddHostedService<OutboxDispatcherHostedService>();

var host = builder.Build();
host.Run();
