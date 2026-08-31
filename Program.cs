using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// Ensure logs go to standard error to prevent corrupting the MCP protocol channel on stdout.
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Bind configuration settings.
builder.Services.Configure<SonarScannerOptions>(
    builder.Configuration.GetSection("SonarScanner"));

// Register the tool service inside the DI container.
builder.Services.AddTransient<SecurityAnalyzerService>();

// Configure the MCP Server to discover instances from the DI engine.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SecurityAnalyzerService>();

// Build and start the host + MCP server.
var host = builder.Build();
await host.RunAsync();

// Strongly typed configuration mapping.
public class SonarScannerOptions
{
    public static readonly string DefaultHostUrl = "http://localhost:9000";
    public static readonly string DefaultToken = string.Empty;

    public string HostUrl { get; set; } = DefaultHostUrl;
    public string Token { get; set; } = DefaultToken;
}

[McpServerToolType]
public class SecurityAnalyzerService(
    IConfiguration configuration,
    ILogger<SecurityAnalyzerService> logger)
{
    private readonly ILogger<SecurityAnalyzerService> _logger = logger;

    private readonly string _sonarHostUrl =
        Environment.GetEnvironmentVariable("SONAR_HOST_URL")
        ?? configuration["SonarScanner:HostUrl"]
        ?? SonarScannerOptions.DefaultHostUrl;

    private readonly string _sonarToken =
        Environment.GetEnvironmentVariable("SONAR_TOKEN")
        ?? configuration["SonarScanner:Token"]
        ?? SonarScannerOptions.DefaultToken;

    /// <summary>
    /// Analyzes a local .NET project for security vulnerabilities using the
    /// dotnet-sonarscanner CLI (begin → build → end workflow).
    /// </summary>
    /// <remarks></remarks>
    [McpServerTool(Name = "ScanProjectAsync")]
    [Description(
        "Analyzes a local .NET project for security vulnerabilities using the " +
        "'dotnet-sonarscanner' CLI. This runs the full begin/build/end workflow " +
        "and uploads the results to the configured SonarQube / SonarCloud server.")]
    public async Task<string> ScanProjectAsync(
        [Description("Absolute path to the .NET project or solution directory to scan.")]
        string projectDirectory,
        [Description("The unique identifier (Key) for the project inside SonarQube / SonarCloud.")]
        string projectKey,
        [Description("Optional: path to a specific solution or project file to build. If omitted, builds the entire directory.")]
        string? projectOrSolutionPath = null,
        [Description("Optional: Git branch name for SonarQube branch analysis.")]
        string? branchName = null,
        [Description("Optional: path to OpenCover coverage report XML file")]
        string? coverageReportPath = null,
        [Description("Optional: comma-separated file/path exclusions appended to defaults (e.g. 'Api/**,Infrastructure/**')")]
        string? exclusions = null,
        [Description("Optional: comma-separated file/path exclusions from coverage calculation only.")]
        string? coverageExclusions = null)
    {
        // ── Validation ──────────────────────────────────────────────────────────
        if (!Directory.Exists(projectDirectory))
        {
            _logger.LogError("Target directory not found: {Dir}", projectDirectory);
            return $"Error: Directory '{projectDirectory}' does not exist.";
        }

        if (string.IsNullOrWhiteSpace(projectKey))
            return "Error: A valid projectKey must be provided.";

        _logger.LogInformation(
            "Starting SonarQube scan for Key: {Key} on host: {Url}",
            projectKey, _sonarHostUrl);

        // ── Step 1: dotnet sonarscanner begin ───────────────────────────────────
        var beginArgs = $"begin /k:\"{projectKey}\" /d:sonar.host.url=\"{_sonarHostUrl}\"";
        if (!string.IsNullOrWhiteSpace(_sonarToken))
            beginArgs += $" /d:sonar.token=\"{_sonarToken}\"";

        if (!string.IsNullOrEmpty(branchName))
        {
            beginArgs += $" /d:sonar.branch.name=\"{branchName}\"";
        }

        if (!string.IsNullOrEmpty(coverageReportPath) && File.Exists(coverageReportPath))
        {
            beginArgs += $" /d:sonar.cs.opencover.reportsPaths=\"{coverageReportPath}\"";
        }

        // These are always excluded. Add anything else to exclude from analysis.
        var baseExclusions = "**/bin/**,**/obj/**,**/.sonarqube/**,**/*.dll,**/*.exe,**/*.pdb,**/node_modules/**,**/dist/**,**/.vite/**";

        // We do not want to scan build artifacts, or configured project-based exclusions.
        if (!string.IsNullOrWhiteSpace(exclusions))
        {
            baseExclusions += "," + exclusions;
        }

        beginArgs += $" /d:sonar.exclusions=\"{baseExclusions}\"";

        // Coverage exclusions: scan for issues but don't count in coverage.
        if (!string.IsNullOrWhiteSpace(coverageExclusions))
        {
            beginArgs += $" /d:sonar.coverage.exclusions=\"{coverageExclusions}\"";
        }

        var beginResult = await RunScannerStepAsync(
            "dotnet", $"sonarscanner {beginArgs}", projectDirectory, isTokenStep: true);

        if (beginResult.ExitCode != 0)
        {
            return $"Scan 'begin' step failed for project key '{projectKey}'.\n{beginResult.CombinedOutput}";
        }

        // ── Step 2: dotnet build ──────────────────────────────────────────────
        var buildArgs = string.IsNullOrWhiteSpace(projectOrSolutionPath)
            ? "build --no-incremental"
            : $"build \"{projectOrSolutionPath}\" --no-incremental";

        var buildResult = await RunScannerStepAsync(
            "dotnet", buildArgs, projectDirectory, isTokenStep: false);

        // Even if build has warnings, we continue to 'end' so the scanner can
        // pick up what analysis it did capture. We'll report build issues later.

        // ── Step 3: dotnet sonarscanner end ─────────────────────────────────────
        var endArgs = "end";
        if (!string.IsNullOrWhiteSpace(_sonarToken))
            endArgs += $" /d:sonar.token=\"{_sonarToken}\"";
        var endResult = await RunScannerStepAsync(
            "dotnet", $"sonarscanner {endArgs}", projectDirectory, isTokenStep: true);

        // ── Compose final report ────────────────────────────────────────────────
        var report = new StringBuilder();
        report.AppendLine($"=== SonarQube Scan Results for '{projectKey}' ===");
        report.AppendLine();
        report.AppendLine("--- 1. Scanner Begin ---");
        report.AppendLine(beginResult.CombinedOutput.TrimEnd());
        report.AppendLine();
        report.AppendLine("--- 2. Build ---");
        report.AppendLine($"Exit code: {buildResult.ExitCode}");
        report.AppendLine(buildResult.CombinedOutput.TrimEnd());
        report.AppendLine();
        report.AppendLine("--- 3. Scanner End ---");
        report.AppendLine($"Exit code: {endResult.ExitCode}");
        report.AppendLine(endResult.CombinedOutput.TrimEnd());
        report.AppendLine();
        report.AppendLine("========================");

        var overallSuccess = beginResult.ExitCode == 0 && endResult.ExitCode == 0;
        report.AppendLine(overallSuccess
            ? "Scan completed and results uploaded to SonarQube."
            : "Scan completed with errors. Review the output above for details.");

        _logger.LogInformation(
            "Scan workflow finished for {Key}. Overall success = {Success}",
            projectKey, overallSuccess);

        return report.ToString();
    }

    /// <summary>
    /// Runs an external process asynchronously, capturing stdout and stderr
    /// without deadlock. Optionally injects the Sonar token via environment
    /// variable for the 'begin' step instead of command-line.
    /// </summary>
    private async Task<ProcessResult> RunScannerStepAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        bool isTokenStep)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Inject token securely via environment variable for the begin step.
        // For end step, the scanner reads the token from the temp files created
        // during begin, so we don't need to pass it again.
        if (isTokenStep && !string.IsNullOrWhiteSpace(_sonarToken))
        {
            psi.EnvironmentVariables["SONAR_TOKEN"] = _sonarToken;
            psi.EnvironmentVariables["SONAR_LOGIN"] = _sonarToken;
            _logger.LogDebug("Injected SONAR_TOKEN and SONAR_LOGIN into process environment.");
        }
        else if (isTokenStep)
        {
            _logger.LogWarning("No Sonar token was provided. Attempting anonymous scan connection.");
        }

        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();

        var process = new Process { StartInfo = psi };

        // Wire up asynchronous readers to avoid deadlocks on large output.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdoutBuffer.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuffer.AppendLine(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch executable '{Exe}'.", fileName);
            return new ProcessResult
            {
                ExitCode = -1,
                CombinedOutput = $"Critical Error: unable to start '{fileName}'. {ex.Message}"
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait asynchronously so we don't block the MCP stdio transport.
        await process.WaitForExitAsync();

        var combined = new StringBuilder();
        if (stdoutBuffer.Length > 0)
        {
            combined.AppendLine("STDOUT:");
            combined.AppendLine(stdoutBuffer.ToString().TrimEnd());
        } 
        if (stderrBuffer.Length > 0)
        {
            combined.AppendLine("STDERR:");
            combined.AppendLine(stderrBuffer.ToString().TrimEnd());
        }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            CombinedOutput = combined.ToString().TrimEnd()
        };
    }

    private sealed class ProcessResult
    {
        public required int ExitCode { get; init; }
        public required string CombinedOutput { get; init; }
    }
}