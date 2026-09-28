using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CreatorPantry.AppHost;

/// <summary>
/// Resolves a connection string for a model served by a local Foundry Local install, by driving its CLI.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because <c>RunAsFoundryLocal()</c> does not work.</strong> Aspire.Hosting.Foundry
/// 13.5.4-preview starts the Foundry service successfully — the service comes up healthy and serves requests —
/// and then marks its own resource <c>FailedToStart</c> about a second later, logging no exception. The one
/// visible oddity is that the CLI's status line reaches the AppHost with its leading emoji mangled, which is
/// what a UTF-8 payload read through the console's ANSI code page looks like; if the integration parses that
/// line for the endpoint, it would fail on output it produced correctly itself. Either way the failure is
/// inside the preview package, and it cascades: every resource that waits on the model deployment fails with
/// it, which takes the whole application down rather than just the AI features.
/// </para>
/// <para>
/// So this drives the same CLI directly and hands the result to the API and Worker as an ordinary connection
/// string — the same path an Azure Foundry deployment arrives by, which is why nothing downstream needs to
/// know. It is imperative code in an AppHost that is otherwise declarative, and that is a cost paid
/// deliberately: the alternative is a developer pasting a port that changes on every service restart.
/// </para>
/// <para>
/// Delete this the day the integration works. <c>Foundry:Enabled</c> still selects the real one.
/// </para>
/// </remarks>
internal static partial class FoundryLocalCli
{
    /// <summary>How long any one CLI call may take. A first model load is slow; a wrong guess here reads as a hang.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(10);

    [GeneratedRegex(@"http://127\.0\.0\.1:\d+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EndpointPattern { get; }

    /// <summary>
    /// Starts the Foundry service if it is not running, loads <paramref name="alias"/>, and returns a
    /// connection string the Azure AI Inference client can use — or null when any step fails.
    /// </summary>
    /// <remarks>
    /// Null rather than throwing: a developer with no Foundry install is the ordinary case, and
    /// <c>AddCreatorPantryAi</c> already falls back to a client that throws only when something actually asks
    /// for a generation. Failing the AppHost here would make an optional feature a prerequisite for starting.
    /// </remarks>
    public static string? TryResolveConnectionString(string alias, out string diagnostic)
    {
        // `service start` is the whole discovery step: it prints the endpoint whether it started the service
        // or found one already running, so there is no second status call and no race between the two.
        if (!TryRun("service start", out var started, out diagnostic))
        {
            return null;
        }

        var endpoint = EndpointPattern.Match(started);
        if (!endpoint.Success)
        {
            diagnostic = $"could not find an endpoint in: {Collapse(started)}";
            return null;
        }

        // Already resident? Then nothing slow happens here at all. This is checked before loading rather than
        // after failing, because the load is the one step that can take minutes: a cold 4GB model onto the GPU
        // overruns the Aspire CLI's 120s start timeout and aborts the launch, and the service outlives an
        // `aspire run`, so after the first start of the day this path is the normal one.
        if (!TryRun("service ps", out var running, out diagnostic))
        {
            return null;
        }

        var modelId = FindModelId(running, alias);

        if (modelId is null)
        {
            // Loading is not optional and not automatic: a service with nothing loaded answers a completion
            // with "No OpenAIService provider found for modelName", which reads like a bad model name rather
            // than a model that is merely not resident yet.
            if (!TryRun($"model load {alias}", out _, out diagnostic))
            {
                return null;
            }

            if (!TryRun("service ps", out running, out diagnostic))
            {
                return null;
            }

            modelId = FindModelId(running, alias);
        }

        // Only that a line for it exists. The id may equal the alias, and legitimately does for a model the
        // catalogue has no alias for -- one cached by a newer CLI than the one installed, which lists it by
        // its full id and prints "Model was not found in catalog" where the alias would go. Treating that
        // equality as failure would reject a model that is loaded and answering.
        if (string.IsNullOrWhiteSpace(modelId))
        {
            diagnostic = $"'{alias}' is not listed as running in: {Collapse(running)}";
            return null;
        }

        diagnostic = $"{endpoint.Value} serving {modelId}";

        // The /v1 suffix is load-bearing. Foundry Local serves the OpenAI routes under /v1, and the Azure
        // client appends "/chat/completions" to whatever endpoint it is given, so the two only meet here.
        // Key is required by the connection-string parser but unused: the local service authenticates nobody.
        return $"Endpoint={endpoint.Value}/v1;Key=unused;DeploymentId={modelId}";
    }

    /// <summary>
    /// The model id on the <c>service ps</c> line for <paramref name="alias"/>, or null when it has none.
    /// </summary>
    /// <remarks>
    /// The id is the last column, and is the resolved variant rather than the alias the catalogue is browsed
    /// by — <c>Phi-4-mini-instruct-cuda-gpu:5</c> for <c>phi-4-mini</c> — so which one it is depends on the
    /// hardware this machine chose. Asking is the only way to know.
    /// </remarks>
    private static string? FindModelId(string servicePs, string alias) =>
        servicePs
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains(alias, StringComparison.OrdinalIgnoreCase))
            ?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();

    private static bool TryRun(string arguments, out string output, out string diagnostic)
    {
        output = string.Empty;
        diagnostic = string.Empty;

        try
        {
            using var process = Process.Start(new ProcessStartInfo("foundry", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // UTF-8 explicitly. The CLI writes emoji, and reading them through the console's ANSI code
                // page is the corruption this file suspects of the integration it replaces.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                diagnostic = "the foundry CLI could not be started";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)CallTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                diagnostic = $"'foundry {arguments}' did not finish within {CallTimeout.TotalMinutes:0} minutes";
                return false;
            }

            output = stdout.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                diagnostic = $"'foundry {arguments}' exited {process.ExitCode}: "
                    + Collapse(output + stderr.GetAwaiter().GetResult());
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Not installed, or not on PATH. The ordinary case on a clean clone, not an error.
            diagnostic = $"the foundry CLI is not available ({ex.Message})";
            return false;
        }
    }

    private static string Collapse(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}
