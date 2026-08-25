#!/usr/bin/env python3
"""
MCP client for invoking the SonarQube security scan tool.
.NET-only version — designed for CI/CD pipelines with explicit exit codes.
"""

import argparse
import asyncio
import os
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def run_scan(
    project_directory: str,
    project_key: str,
    project_file: str | None,
    server_dll_path: str,
    sonar_host_url: str | None,
    sonar_token: str | None,
    branch_name: str | None,
    coverage_report: str | None,
    exclusions: str | None,
) -> int:
    """
    Spawns the MCP server and calls ScanProjectAsync.
    Returns 0 on success, 1 on scan failure, 2 on connection/tool error.
    """

    if not os.path.isfile(server_dll_path):
        print(f"[ERROR] MCP server DLL not found: {server_dll_path}", file=sys.stderr)
        print("[HINT] Set SONAR_MCP_DLL env var or pass --server-dll", file=sys.stderr)
        return 2

    # Merge with full parent environment — replacing the entire env breaks PATH
    # and other required variables on macOS self-hosted runners.
    env = dict(os.environ)
    if sonar_host_url:
        env["SONAR_HOST_URL"] = sonar_host_url
    if sonar_token:
        # Some older scanner versions expect SONAR_LOGIN instead of SONAR_TOKEN.
        # Setting both ensures compatibility across scanner versions.
        env["SONAR_TOKEN"] = sonar_token
        env["SONAR_LOGIN"] = sonar_token

    # ── Diagnostics: log what the server subprocess will receive ──
    print("=== Subprocess Environment Diagnostics ===")
    print(f"Command: dotnet {server_dll_path}")
    print(f"SONAR_HOST_URL: {env.get('SONAR_HOST_URL', 'NOT SET')}")
    token_status = "SET" if env.get('SONAR_TOKEN') else "NOT SET"
    print(f"SONAR_TOKEN: {token_status}")
    print(f"SONAR_LOGIN: {token_status}")
    path_env = env.get('PATH', '')
    dotnet_tools_in_path = any('dotnet' in p.lower() for p in path_env.split(':'))
    print(f".dotnet/tools in PATH: {dotnet_tools_in_path}")
    print(f"PATH first 3 entries: {':'.join(path_env.split(':')[:3])}")
    print("=========================================")

    server = StdioServerParameters(
        command="dotnet",
        args=[server_dll_path],
        env=env,
    )

    async with stdio_client(server) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            # Optional: list tools to verify the server is healthy
            tools = await session.list_tools()
            tool_names = [t.name for t in tools.tools]
            print(f"[MCP] Connected. Available tools: {tool_names}")

            tool_name = "scan_project" if "scan_project" in tool_names else "ScanProjectAsync"
            if tool_name not in tool_names:
                print(f"[ERROR] scan_project or ScanProjectAsync not found in server tools: {tool_names}", file=sys.stderr)
                return 2

            # Call the scan tool
            arguments = {
                "projectDirectory": project_directory,
                "projectKey": project_key,
            }
            if project_file:
                arguments["projectOrSolutionPath"] = project_file
            if branch_name:
                arguments["branchName"] = branch_name
            if coverage_report:
                arguments["coverageReportPath"] = coverage_report
            if exclusions:
                arguments["exclusions"] = exclusions

            print(f"[MCP] Starting scan: projectKey={project_key}, dir={project_directory}, tool={tool_name}")
            result = await session.call_tool(tool_name, arguments=arguments)

            # Extract text output
            text = next(
                (c.text for c in result.content if hasattr(c, "text")), None
            )
            if not text:
                print("[WARN] Tool returned no text content.", file=sys.stderr)
                text = str(result.content)

            print("\n=== SCAN OUTPUT ===")
            print(text)
            print("===================")

            # Verify scan was uploaded by querying SonarQube API directly.
            # This is more reliable than parsing text output, which varies by
            # scanner version and may not contain expected success strings.
            api_success = _verify_scan_uploaded(
                sonar_host_url=sonar_host_url,
                sonar_token=sonar_token,
                project_key=project_key,
            )

            text_success = "uploaded to SonarQube" in text or "Scan completed and results uploaded" in text

            if api_success or text_success:
                print("\nSUCCESS: Scan completed and results uploaded.")
                return 0

            print("\nFAILURE: Scan did not report success.", file=sys.stderr)
            return 1


def _verify_scan_uploaded(
    sonar_host_url: str | None,
    sonar_token: str | None,
    project_key: str,
) -> bool:
    """
    Query SonarQube API to verify the project has a RECENT analysis.
    Returns True only if the latest analysis is from the last 10 minutes.
    Prevents false positives from stale scan data.
    """
    if not sonar_host_url or not sonar_token:
        return False

    try:
        import urllib.request
        import base64
        import json
        from datetime import datetime, timezone, timedelta

        url = f"{sonar_host_url.rstrip('/')}/api/qualitygates/project_status?projectKey={project_key}"
        auth = base64.b64encode(f"{sonar_token}:".encode()).decode()

        req = urllib.request.Request(
            url,
            headers={
                "Authorization": f"Basic {auth}",
                "Accept": "application/json",
            },
        )

        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read())
            project_status = data.get("projectStatus", {})
            status = project_status.get("status")
            analysis_date_str = project_status.get("analysisDate")

            if status not in ("OK", "ERROR", "WARN"):
                return False

            # Check analysis is recent (within last 10 minutes)
            if analysis_date_str:
                try:
                    # SonarQube dates are ISO 8601, e.g. 2026-08-06T03:27:39+0000
                    analysis_date = datetime.fromisoformat(analysis_date_str.replace('+0000', '+00:00'))
                    age = datetime.now(timezone.utc) - analysis_date
                    if age < timedelta(minutes=10):
                        return True
                    print(f"[WARN] Analysis is {age.total_seconds()/60:.0f} minutes old — not a fresh scan", file=sys.stderr)
                except Exception:
                    pass

            return False
    except Exception as ex:
        print(f"[WARN] API verification failed: {ex}", file=sys.stderr)
        return False


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Invoke SonarQube security scan via MCP stdio server."
    )
    parser.add_argument(
        "project_directory",
        help="Absolute path to the .NET project or solution directory to scan.",
    )
    parser.add_argument(
        "project_key",
        help="The unique project key inside SonarQube.",
    )
    parser.add_argument(
        "--project-file",
        help="Optional: path to a specific solution or project file to build.",
    )
    parser.add_argument(
        "--server-dll",
        default=os.environ.get("SONAR_MCP_DLL"),
        help="Path to the DotNetSecurityMcpServer DLL. Required unless SONAR_MCP_DLL env var is set.",
    )
    parser.add_argument(
        "--sonar-host-url",
        default=os.environ.get("SONAR_HOST_URL"),
        help="SonarQube server URL.",
    )
    parser.add_argument(
        "--sonar-token",
        default=os.environ.get("SONAR_TOKEN"),
        help="SonarQube authentication token.",
    )
    parser.add_argument(
        "--branch-name",
        default=None,
        help="Git branch name for SonarQube branch analysis. Only pass this if using SonarQube Developer Edition or above.",
    )
    parser.add_argument(
        "--coverage-report",
        default=None,
        help="Path to an OpenCover or Cobertura coverage report XML file.",
    )
    parser.add_argument(
        "--exclusions",
        default=None,
        help='Comma-separated file/path exclusions appended to defaults (e.g. "Api/**,Infrastructure/**").',
    )

    args = parser.parse_args()

    try:
        exit_code = asyncio.run(
            run_scan(
                project_directory=args.project_directory,
                project_key=args.project_key,
                project_file=args.project_file,
                server_dll_path=args.server_dll,
                sonar_host_url=args.sonar_host_url,
                sonar_token=args.sonar_token,
                branch_name=args.branch_name,
                coverage_report=args.coverage_report,
                exclusions=args.exclusions,
            )
        )
    except Exception as ex:
        import traceback
        print(f"[FATAL] {ex}", file=sys.stderr)
        traceback.print_exc()
        sys.exit(3)

    sys.exit(exit_code)


if __name__ == "__main__":
    main()
