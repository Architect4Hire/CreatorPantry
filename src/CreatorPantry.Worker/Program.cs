using CreatorPantry.Domain.Time;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApplicationTime();

var host = builder.Build();
host.Run();
