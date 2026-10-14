using System.Diagnostics;
using System.Globalization;
using CsWebUi.Native;
using CsWebUi.Tests.Stress;

// Shutdown stress for runic-sdk#239.
//
//   host   [--iterations N] [--pending M] [--seed S]
//     Runs N host lifetimes in this process, each shut down with M replies pending.
//   driver [--runs R] [--parallel P] [--iterations N] [--pending M] [--timeout-seconds T]
//     Starts R host processes, P at a time, and reports how many crashed or hung. A crash
//     kills a whole process, so each run is a separate process.

var arguments = Arguments.Parse(args);
var libraryPath = Environment.GetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY");
if (!string.IsNullOrWhiteSpace(libraryPath))
{
    WebUiNativeLibrary.SetLibraryPath(libraryPath);
}

return arguments.Mode switch
{
    "host" => await RunHostAsync(arguments).ConfigureAwait(false),
    "driver" => await RunDriverAsync(arguments).ConfigureAwait(false),
    _ => Usage(),
};

static async Task<int> RunHostAsync(Arguments arguments)
{
    var iterations = arguments.GetInt32("iterations", 1);
    var pending = arguments.GetInt32("pending", 32);
    var random = new Random(arguments.GetInt32("seed", Environment.ProcessId));
    for (var i = 0; i < iterations; i++)
    {
        await ShutdownStressScenario.RunOnceAsync(pending, random, CancellationToken.None).ConfigureAwait(false);
    }

    return 0;
}

static async Task<int> RunDriverAsync(Arguments arguments)
{
    var runs = arguments.GetInt32("runs", 100);
    var parallel = arguments.GetInt32("parallel", Math.Max(1, Environment.ProcessorCount / 2));
    var iterations = arguments.GetInt32("iterations", 1);
    var pending = arguments.GetInt32("pending", 32);
    var timeout = TimeSpan.FromSeconds(arguments.GetInt32("timeout-seconds", 120));

    var (fileName, prefix) = SelfCommand();
    var failures = new List<string>();
    var completed = 0;
    var stopwatch = Stopwatch.StartNew();
    using var gate = new SemaphoreSlim(parallel);

    var tasks = Enumerable.Range(0, runs).Select(async run =>
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var outcome = await RunChildAsync(
                fileName,
                [.. prefix, "host", "--iterations", Invariant(iterations), "--pending", Invariant(pending), "--seed", Invariant(run)],
                timeout).ConfigureAwait(false);
            lock (failures)
            {
                completed++;
                if (outcome is not null)
                {
                    failures.Add($"run {run}: {outcome}");
                    Console.WriteLine($"run {run}: {outcome}");
                }
            }
        }
        finally
        {
            gate.Release();
        }
    });
    await Task.WhenAll(tasks).ConfigureAwait(false);

    var shutdowns = runs * iterations;
    Console.WriteLine(
        $"{runs} runs x {iterations} shutdowns with {pending} pending replies: {failures.Count} failed runs, " +
        $"{runs - failures.Count} clean, in {stopwatch.Elapsed.TotalSeconds:F1} s ({shutdowns} shutdowns).");
    return failures.Count == 0 ? 0 : 1;
}

static async Task<string?> RunChildAsync(string fileName, IReadOnlyList<string> childArguments, TimeSpan timeout)
{
    var startInfo = new ProcessStartInfo(fileName)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in childArguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start a host process.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var cancellation = new CancellationTokenSource(timeout);
    try
    {
        await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return $"hung for more than {timeout.TotalSeconds:F0} s";
    }

    if (process.ExitCode == 0)
    {
        return null;
    }

    var stderr = (await error.ConfigureAwait(false)).Trim();
    var stdout = (await output.ConfigureAwait(false)).Trim();
    var detail = stderr.Length > 0 ? stderr : stdout;
    if (detail.Length > 400)
    {
        detail = detail[..400] + " ...";
    }

    return $"exit code {process.ExitCode}{Describe(process.ExitCode)}: {detail}";
}

static string Describe(int exitCode) => exitCode switch
{
    134 => " (SIGABRT)",
    139 => " (SIGSEGV)",
    135 => " (SIGBUS)",
    -1073741819 => " (access violation)",
    _ => string.Empty,
};

static (string FileName, string[] Prefix) SelfCommand()
{
    var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");
    var assembly = typeof(ShutdownStressScenario).Assembly.Location;
    return Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? (processPath, [assembly])
        : (processPath, []);
}

static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

static int Usage()
{
    Console.Error.WriteLine("usage: CsWebUi.ShutdownStress host|driver [--option value]...");
    return 2;
}

internal sealed class Arguments
{
    private readonly Dictionary<string, string> _options;

    private Arguments(string mode, Dictionary<string, string> options)
    {
        Mode = mode;
        _options = options;
    }

    internal string Mode { get; }

    internal static Arguments Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i + 1 < args.Length; i += 2)
        {
            options[args[i].TrimStart('-')] = args[i + 1];
        }

        return new Arguments(args.Length > 0 ? args[0] : string.Empty, options);
    }

    internal int GetInt32(string name, int defaultValue)
        => _options.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : defaultValue;
}
