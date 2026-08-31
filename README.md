# DotNetSecurityMcpServer

## Purpose

This project was created to demonstrate the practical creation and interoperability of a simple, useful **Model Context Protocol (MCP) server** and **Model Context Protocol (MCP) client**. The original idea was to create a **reusable, language-agnostic security scanning service**

## What It Is

The `DotNetSecurityMcpServer` is a **Model Context Protocol (MCP) server** written in C# .NET that wraps `dotnet-sonarscanner` as an MCP tool. It exposes a single tool — `ScanProjectAsync` — which CI/CD pipelines (GitHub Actions, Azure DevOps, Jenkins) can invoke through any MCP client (C#, Python, or PowerShell), to run a SonarQube security scan. A Python Client (Cross-Platform / Reference Implementation) named `sonar_scan_client.py` is included.

## The Positives

What are the benefits of wrapping `dotnet-sonarscanner` as an MCP server?

Wrapping `dotnet-sonarscanner` (or the underlying SonarQube / SonarCloud capabilities) as a Model Context Protocol (MCP) server bridges the gap between raw static code analysis and autonomous AI coding agents. It transforms a rigid CLI scanner into an interactive tool that an AI assistant can use dynamically.

The core benefits of this integration include:

1. Eliminating Context Switching

### In-IDE Verification

Developers no longer need to switch tabs between an AI-native IDE (like Cursor or Claude Code) and the SonarQube web dashboard.

### Seamless Feedback Loops

The AI agent can request an analysis or pull metrics using natural language right inside the workspace, maintaining a real-time conversational workflow.

1. Enabling Autonomous "Agentic" Code Repair

### Self-Healing Code

Rather than just alerting human developers to bugs, code smells, or vulnerabilities, an AI agent can actively trigger a scan, read the resulting diagnostics, and immediately rewrite the .NET code to resolve the issue.

### Automated Pull Request Verification

Agents can pull code quality issues directly from SonarQube, fix them on a feature branch, and push clean code before the manual peer-review process even begins.

1. Native Conversational Auditing

### Natural Language Queries

Developers or AI agents can ask complex questions such as "Are there any new security hotspots in this project?" or "What is our current quality gate status?"

### Dynamic Translation

The MCP server translates these plain-English queries into precise SonarQube API calls (e.g., executing search_sonar_issues_in_projects), fetching relevant line-by-line coverage details natively.

1. Standardizing the AI Toolset (Solving the M×N Integration Problem)

### Model-Agnostic Interface

Instead of writing proprietary, one-off plugins for every distinct AI assistant (GitHub Copilot, Gemini CLI, Claude, Windsurf), you wrap the dotnet-sonarscanner functionality once using the open Model Context Protocol standard.

### Universal Compatibility

Any client that supports the MCP standard can immediately consume your .NET code analysis tools out-of-the-box.

1. On-Demand Analysis of Code Snippets

### Pre-commit Guardrails

Traditional dotnet-sonarscanner workflows typically run during heavy CI/CD pipelines. An MCP server enables the agent to evaluate local file states or code snippets before they are even compiled or committed, saving significant cloud build time.

## The Negatives

What are the disadvantages of wrapping `dotnet-sonarscanner` as an MCP server? There is one major one.

### Unecessarily Complex

1. Local Development

The MCP server adds complexity (extra process, stdio transport, version matching between client and server) without providing value for a single-purpose CI scan. Direct CLI calls are simpler, faster, and have fewer failure modes. In other words, the extra layers add fragility. For example, if using github as your source repository, you can use a direct `dotnet sonarscanner` CLI invocation using a workflow as in `.github/workflows/<your-security-scan.yaml>` The latter can be set up to run on a github self-hosted runner without MCP indirection, which is much more efficient.

| Concern | MCP Server Approach | Direct CLI Approach |
| --------- | ------------------- | ------------------- |
| **Complexity** | Spawns child process, manages stdio transport, JSON-RPC protocol | Single `dotnet sonarscanner begin/build/end` command |
| **Failure modes** | Server start failure, version mismatch between client and server `ModelContextProtocol` package, stdio deadlock | Scanner CLI exits with code; straightforward to debug |
| **CI time** | ~3-5s server startup + client initialization | Zero overhead |
| **Portability** | Requires `dotnet` on runner to start server; Python `mcp` package for client | Only requires `dotnet-sonarscanner` tool |
| **Maintenance** | Must keep client/server NuGet versions in sync | No version coupling |
| **Value** | Indirection for hypothetical future tools | Single-purpose, immediately understandable |

## Architecture Overview

```

┌─────────────────┐     stdio      ┌──────────────────────┐     HTTP     ┌─────────────┐
│   CI/CD Agent   │ ─────────────> │ DotNetSecurityMcp    │ ───────────> │  SonarQube  │
│  (Client)       │   JSON-RPC     │ Server (dotnet DLL)  │   Scanner    │  (IP:port)  |
└─────────────────┘                └──────────────────────┘              └─────────────┘

    1. Spawns server process
    2. Sends JSON-RPC `tools/call` request
    3. Server runs begin → build → end
    4. Returns scan output as text

```

## MCP Server

### Location

Release build:

`<your-base-path>\DotNetSecurityMcpServer\bin\Release\net10.0\DotNetSecurityMcpServer.dll`

### Configuration

Add to your MCP client configuration (e.g., Claude Desktop, VS Code, or custom clients):

```json
{
  "sonarqube": {
    "transport": "stdio",
    "command": "dotnet",
    "args": [
      "<your-base-path>/DotNetSecurityMcpServer/bin/Release/net10.0/DotNetSecurityMcpServer.dll"
    ],
    "env": {
      "SONAR_HOST_URL": "http://<your-sonar-ip:port>",
      "SONAR_TOKEN": "<your-sonar-token>"
    },
    "enabled": true
  }
}
```

### Program.cs

The server was written to address critical functional and security issues:

1. **Full 3-step workflow:** `begin` → `build --no-incremental` → `end`
2. **Async signature:** `public async Task<string> ScanProjectAsync(...)` with `await process.WaitForExitAsync()`
3. **Secure token handling:** Token injected via `ProcessStartInfo.EnvironmentVariables["SONAR_TOKEN"]` instead of CLI arguments (prevents exposure in process lists and logs)
4. **Deadlock-safe output capture:** Uses `BeginOutputReadLine()` / `BeginErrorReadLine()` with async event handlers
5. **Optional build target:** Supports `projectOrSolutionPath` parameter for specific solution/project files

---

## MCP Client

To invoke the `ScanProjectAsync` tool you need an MCP client. The client can be developed in C# .NET, Powershell, Python, etc. The choice would typically be made based on your pipeline environment.

---

## Client Selection Guide

| Environment | Recommended Client | Reason |
| ------------- | ------------------- | -------- |
| .NET-centric CI (Azure DevOps Windows) | **C#** | Type safety, native `Task` support, integrates with existing .NET build steps |
| Linux/macOS CI (GitHub Actions Ubuntu) | **Python** | Reference SDK is most stable, Python pre-installed |
| SDK stability concerns | **PowerShell** | Raw JSON-RPC has zero dependency on preview SDK versions |
| Quick local testing | **Python** | Fastest to set up (`pip install mcp`) |

---

### Azure DevOps Example (Python Client)

```yaml
steps:
  - task: UsePythonVersion@0
    inputs:
      versionSpec: '3.12'

  - script: pip install mcp
    displayName: 'Install MCP SDK'

  - powershell: |
      $env:SONAR_HOST_URL = "$(SonarHostUrl)"
      $env:SONAR_TOKEN    = "$(SonarToken)"
      python scripts/sonar_scan_client.py `
        "$(Build.SourcesDirectory)\src\Api" `
        "my-api-project"
    displayName: 'Run SonarQube Scan (Python MCP Client)'
```

### Azure DevOps Example (C# Client)

```yaml
steps:
  - task: DotNetCoreCLI@2
    displayName: 'Build Sonar MCP Client'
    inputs:
      command: 'build'
      projects: '<your-ci-folder>/SonarMcpClient/SonarMcpClient.csproj'
      arguments: '-c Release'

  - powershell: |
      $env:SONAR_HOST_URL = "$(SonarHostUrl)"
      $env:SONAR_TOKEN    = "$(SonarToken)"
      & "<your-ci-folder>/SonarMcpClient/bin/Release/net10.0/SonarMcpClient.exe" `
        "$(Build.SourcesDirectory)\src\Api" `
        "my-api-project"
    displayName: 'Run SonarQube Security Scan'
```

## GitHub Actions Integration

To be addressed in the future. We will discuss running the client manually.

---

## Running the Python MCP Client for SonarQube Scans (Local/Windows)

## Prerequisites

1. **Java 17 or 21** installed and `JAVA_HOME` set
   - SonarScanner requires Java 17+ (class file version 61.0)
   - Download from [Eclipse Temurin](https://adoptium.net/) or [Microsoft OpenJDK](https://learn.microsoft.com/en-us/java/openjdk/download)
   - Verify: `$env:JAVA_HOME` returns a valid JDK path

2. **Python 3** installed with `mcp` package

   ```powershell
   pip install mcp
   ```

3. **dotnet-sonarscanner** tool installed globally

   ```powershell
   dotnet tool install --global dotnet-sonarscanner
   ```

4. **The MCP Server DLL** built (archived, but functional)
   - Path: `<your-base-path>\DotNetSecurityMcpServer\bin\Release\net10.0\DotNetSecurityMcpServer.dll`

5. **Environment variables**

   ```powershell
   $env:SONAR_HOST_URL = "http://<your-sonar-ip:port>"
   $env:SONAR_TOKEN = "squ_<your-token>"  # Your SonarQube token
   $env:JAVA_HOME = "C:\Program Files\Eclipse Adoptium\jdk-17.0.12.7-hotspot" # Typical location
   ```

---

## Common Errors and Solutions

### Error: "Specify a project or solution file"

```
MSBUILD : error MSB1003: Specify a project or solution file.
```

**Cause:** The working directory doesn't contain a `.csproj` or `.sln` file at its root.

**Fix:** Pass the `--project-file` argument:

```powershell
--project-file "<your-src-folder>\ConsultingSite.slnx"
```

### Error: "Could not find Java"

```
'JAVA_HOME' environment variable not set
java.lang.UnsupportedClassVersionError: ... class file version 61.0
```

**Cause:** Java 8 (version 52.0) found, but SonarScanner needs Java 17 (version 61.0).

**Fix:** Set `JAVA_HOME` to a Java 17 or 21 JDK:

```powershell
$env:JAVA_HOME = "C:\Program Files\Eclipse Adoptium\jdk-17.0.12.7-hotspot"
```

### Error: "0% coverage, 98 issues"

**Cause:** As an example, suppose your backend C# .NET source code was contained in a parent folder named 'src'. Assume further that you are not unit testing, but you are integration testing, backend code contained in the sub-folders src/Api, and src/Infrastructure, and that the scanner analyzed the entire repo root directory, including:

- `<your-site-folder>/` (React/TypeScript)
- `<your-ai-ide-config-folder>/` (scripts and tools)
- `<your-client-scripts-folder>/` (client scripts)
- `<your-tests-folder>/` (test code)
- `<your-utility-scripts-folder>` (utility script)

**Fix:** Pass `--exclusions` to limit analysis to the backend project:

```powershell
--exclusions "src/Api/**,src/Infrastructure/**,<your-site-folder>/**,<your-ai-ide-config-folder>/**,<your-client-scripts-folder>/**,<your-tests-folder>/**,<your-utility-scripts-folder>" # Example locations described above
```

Also: **run tests with coverage first**, then pass the report to `--coverage-report`:

```powershell
dotnet test tests\unit\UnitTests.csproj -c Release `
  /p:CollectCoverage=true `
  /p:CoverletOutputFormat=opencover `
  /p:CoverletOutput="<your-unit-tests-base-path>\TestResults\coverage.xml"
```

---

## Complete Command Sequence

```powershell
# 1. Set environment variables
$env:SONAR_HOST_URL = "http://<your-sonar-ip:port>"
$env:SONAR_TOKEN = "squ_<your-token>"
$env:JAVA_HOME = "C:\Program Files\Eclipse Adoptium\jdk-17.0.12.7-hotspot"

# 2. Run tests with coverage
dotnet test "<your-unit-tests-base-path>\UnitTests.csproj" -c Release `
  /p:CollectCoverage=true `
  /p:CoverletOutputFormat=opencover `
  /p:CoverletOutput="<your-unit-tests-base-path>\TestResults\coverage.xml"

# 3. Run the Python MCP client with all parameters
python "<your-solution-base-path>\DotNetSecurityMcpServer\PythonMcpClient\sonar_scan_client.py" `
  "<your-scanned-project-path>" `
  "<sonar-project-name>" `
  --server-dll "<your-solution-base-path>\DotNetSecurityMcpServer\bin\Release\net10.0\DotNetSecurityMcpServer.dll" `
  --project-file "<your-src-folder>\ConsultingSite.slnx" `
  --coverage-report "<your-unit-tests-base-path>\TestResults\coverage.xml" `
  --exclusions "src/Api/**,src/Infrastructure/**,<your-site-folder>/**,<your-ai-ide-config-folder>/**,<your-client-scripts-folder>/**,<your-tests-folder>/**,<your-utility-scripts-folder>" # Example locations 

# 4. Verify the scan uploaded
# Query SonarQube API directly:
# (Replace with your actual token)
$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("$env:SONAR_TOKEN:"))
Invoke-RestMethod -Uri "http://<your-sonar-ip:port>/api/qualitygates/project_status?projectKey=<your-project-key>" `
  -Headers @{ Authorization = "Basic $auth" }
```

---

## Expected Results

When run correctly:

- **Coverage:** ~68.2% (reflects the actual backend unit test coverage)
- **Issues:** 0 (backend only, with exclusions applied)
- **Quality Gate:** OK

When run incorrectly (missing exclusions/coverage):

- **Coverage:** 0.0%
- **Issues:** 98+ (analyzing frontend scripts, TypeScript, Python files, etc.)
- **Quality Gate:** ERROR (new violations, 0% coverage)

---

## Why Use MCP Client Instead of Direct CLI?

The MCP client is useful for:

1. **Testing the archived server** — verifying the old DLL still works
2. **Local debugging** — inspecting the JSON-RPC communication
3. **Learning/exploration** — understanding how MCP tool invocation works

The MCP client adds ~3-5s overhead (server startup) and has more failure modes (version mismatch, stdio deadlock). To reiterate, for CI, direct CLI is simpler.

---
