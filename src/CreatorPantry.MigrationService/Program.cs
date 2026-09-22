using CreatorPantry.MigrationService;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddMigrationDatabase();
builder.Services.AddMigrationHost();

var host = builder.Build();

// Resolve before running: RunAsync disposes the host and its service provider.
var runState = host.Services.GetRequiredService<MigrationRunState>();
await host.RunAsync();

return runState.ExitCode;
