#!/usr/bin/env python3
"""
Check that the application source code contains no network, telemetry or logging APIs.

The application must work fully offline (see DESIGN.md, "Offline and Security
Requirements"). This is a cheap static guard against accidentally introducing network
access, telemetry, crash reporting or logging of private texts.
"""

import pathlib
import re
import sys
from typing import List, Tuple

# (pattern, reason)
FORBIDDEN: List[Tuple[str, str]] = [
    (r"\bHttpClient\b", "HTTP client"),
    (r"\bHttpWebRequest\b|\bWebRequest\b|\bWebClient\b", "web request"),
    (r"\bSystem\.Net\.Http\b", "HTTP namespace"),
    (r"\bSystem\.Net\.Sockets\b|\bTcpClient\b|\bUdpClient\b|\bSocket\b", "sockets"),
    (r"\bHttpListener\b|\bKestrel\b|\bWebApplication\b", "local HTTP server"),
    (r"\bWebSocket\b", "web sockets"),
    (r"\bDns\.", "DNS lookups"),
    (r"\bProcess\.Start\b", "spawning processes (e.g. Ollama, browsers)"),
    (
        r"\bApplicationInsights\b|\bTelemetryClient\b|\bSentry\b|\bOpenTelemetry\b",
        "telemetry",
    ),
    (r"\bILogger\b|\bLoggerFactory\b|\bSerilog\b|\bNLog\b|\blog4net\b", "logging"),
    (r"\bTrace\.Write|\bDebug\.Write", "trace output"),
    (r"\bLogToTrace\b", "Avalonia trace logging"),
    (r"\bPath\.GetTempFileName\b", "temporary files"),
    (r"\bollama\b|\blocalhost\b|\b127\.0\.0\.1\b", "external inference servers"),
]

ALLOWED_SUFFIXES = {".cs", ".axaml", ".csproj"}


def main() -> int:
    """Execute the main routine."""
    repo_root = pathlib.Path(__file__).resolve().parent.parent.parent
    src = repo_root / "src"

    compiled = [
        (re.compile(pattern, re.IGNORECASE), reason) for pattern, reason in FORBIDDEN
    ]

    problems: List[str] = []
    for path in sorted(src.rglob("*")):
        if path.suffix not in ALLOWED_SUFFIXES or any(
            part in ("bin", "obj") for part in path.parts
        ):
            continue
        for line_no, line in enumerate(
            path.read_text(encoding="utf-8").splitlines(), start=1
        ):
            for regex, reason in compiled:
                if regex.search(line):
                    rel = path.relative_to(repo_root)
                    problems.append(f"{rel}:{line_no}: {reason}: {line.strip()}")

    # Avalonia's build telemetry must stay disabled for every build, even without the
    # AVALONIA_TELEMETRY_OPTOUT environment variable.
    targets = repo_root / "Directory.Build.targets"
    if not targets.exists() or not re.search(
        r'<Target\s+Name="AvaloniaStats"\s*/>', targets.read_text(encoding="utf-8")
    ):
        problems.append(
            f"{targets.relative_to(repo_root)}: the empty AvaloniaStats target, which "
            "disables Avalonia's build telemetry, is missing"
        )

    if problems:
        print("Forbidden APIs found in the application source:", file=sys.stderr)
        for problem in problems:
            print(f"  {problem}", file=sys.stderr)
        return 1

    print(
        "No network, telemetry or logging APIs found in src/; "
        "Avalonia build telemetry is disabled."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
