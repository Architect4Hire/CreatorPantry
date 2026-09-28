using CreatorPantry.ServiceDefaults;
using CreatorPantry.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddWorkerServices();

var host = builder.Build();
host.Run();
