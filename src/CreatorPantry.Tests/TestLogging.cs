using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests;

/// <summary>
/// Keeps the hosts a test starts from writing request and SQL logs nobody reads, and from being kept in
/// memory after they are disposed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Levels.</strong> Several thousand tests each start a real API host, and most a real Gateway too.
/// Left at an environment's own levels, a full run wrote half a gigabyte of schema DDL and request logs to the
/// console, and each host's captured log held every SQL statement it ran. The levels are set as environment
/// variables, once, before any test runs, because that is the one configuration source every host in this
/// assembly reads — <c>SqliteApiHost</c>, <c>GatewayTestHost</c> and the factories built directly — and it
/// outranks <c>appsettings.Development.json</c>. A host's own <c>CapturingLoggerProvider</c> still receives
/// everything the application logs at Information; only EF Core's own categories are raised to Warning.
/// </para>
/// <para>
/// <strong>Providers.</strong> See <see cref="RemoveProcessWideLoggers"/>, which the two shared hosts call.
/// </para>
/// </remarks>
internal static class TestLogging
{
    [ModuleInitializer]
    internal static void Quiet()
    {
        Environment.SetEnvironmentVariable("Logging__Console__LogLevel__Default", "None");
        Environment.SetEnvironmentVariable("Logging__Debug__LogLevel__Default", "None");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore", "Warning");
    }

    /// <summary>
    /// Takes the console, debug and EventSource loggers out of a host under test, so that a finished host can
    /// be collected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silencing them is not enough. The console provider starts a thread of its own while the host is being
    /// built, and a thread keeps the execution context it was started in — which, under
    /// <c>WebApplicationFactory</c>, holds the host builder and through it the whole host. The runtime keeps a
    /// finished thread's object until nothing refers to it, the provider refers to it, and the host refers to
    /// the provider: a cycle with a root, so no host a test ever built was collected. The EventSource provider
    /// ties each host to one process-wide object in the same way.
    /// </para>
    /// <para>
    /// That was about ten megabytes a test and twelve gigabytes by the end of a run, with the garbage
    /// collector stopping the process for seconds at a time. Those pauses outlasted the Gateway's ten-second
    /// timeout on its calls to the API, which is what the <c>Sign-in failed … InternalServerError</c> scattered
    /// through a full run was. Nothing reads any of the three in a test; a host's own
    /// <c>CapturingLoggerProvider</c> is untouched.
    /// </para>
    /// </remarks>
    internal static void RemoveProcessWideLoggers(IServiceCollection services)
    {
        string[] assemblies =
        [
            "Microsoft.Extensions.Logging.Console",
            "Microsoft.Extensions.Logging.Debug",
            "Microsoft.Extensions.Logging.EventSource",
        ];

        foreach (var descriptor in services
            .Where(descriptor => !descriptor.IsKeyedService
                && (descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType()) is { } type
                && assemblies.Contains(type.Assembly.GetName().Name))
            .ToList())
        {
            services.Remove(descriptor);
        }
    }
}
