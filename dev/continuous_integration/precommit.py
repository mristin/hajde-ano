#!/usr/bin/env python3
"""Run pre-commit checks on the repository."""

import argparse
import enum
import os
import pathlib
import shlex
import subprocess
import sys
from typing import Mapping, Optional, Sequence

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

from dotnet_env import dotnet_env


class Step(enum.Enum):
    """Enumerate different pre-commit steps."""

    REFORMAT_CSHARP = "reformat-csharp"
    REFORMAT_PYTHON = "reformat-python"
    LINT_CSHARP = "lint-csharp"
    LINT_PYTHON = "lint-python"
    MYPY = "mypy"
    CHECK_OFFLINE = "check-offline"
    TEST = "test"


def call_and_report(
    verb: str,
    cmd: Sequence[str],
    cwd: Optional[pathlib.Path] = None,
    env: Optional[Mapping[str, str]] = None,
) -> int:
    """
    Wrap a subprocess call with the reporting to STDERR if it failed.

    Return 1 if there is an error and 0 otherwise.
    """
    cmd_str = " ".join(shlex.quote(part) for part in cmd)

    if cwd is not None:
        print(f"Executing from {cwd}: {cmd_str}")
    else:
        print(f"Executing: {cmd_str}")

    exit_code = subprocess.call(cmd, cwd=str(cwd) if cwd is not None else None, env=env)

    if exit_code != 0:
        print(
            f"Failed to {verb} with exit code {exit_code}: {cmd_str}", file=sys.stderr
        )

    return exit_code


PYTHON_TARGETS = ["dev"]


def main() -> int:
    """Execute entry_point routine."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--overwrite",
        help="Try to automatically fix the offending files (e.g., by re-formatting).",
        action="store_true",
    )
    parser.add_argument(
        "--select",
        help=(
            "If set, only the selected steps are executed. "
            "This is practical if some of the steps failed and you want to "
            "fix them in isolation. "
            "The steps are given as a space-separated list of: "
            + " ".join(value.value for value in Step)
        ),
        metavar="",
        nargs="+",
        choices=[value.value for value in Step],
    )
    parser.add_argument(
        "--skip",
        help=(
            "If set, skips the specified steps. "
            "This is practical if some of the steps passed and "
            "you want to fix the remainder in isolation. "
            "The steps are given as a space-separated list of: "
            + " ".join(value.value for value in Step)
        ),
        metavar="",
        nargs="+",
        choices=[value.value for value in Step],
    )

    args = parser.parse_args()

    overwrite = bool(args.overwrite)

    selects = (
        [Step(value) for value in args.select]
        if args.select is not None
        else list(Step)
    )
    skips = [Step(value) for value in args.skip] if args.skip is not None else []

    repo_root = pathlib.Path(os.path.realpath(__file__)).parent.parent.parent

    env = dotnet_env()

    def enabled(step: Step) -> bool:
        return step in selects and step not in skips

    if enabled(Step.REFORMAT_CSHARP):
        print("Re-formatting C# with CSharpier...")
        exit_code = call_and_report(
            verb="restore the dotnet tools",
            cmd=["dotnet", "tool", "restore"],
            cwd=repo_root,
            env=env,
        )
        if exit_code != 0:
            return 1

        exit_code = call_and_report(
            verb="format with CSharpier" if overwrite else "check with CSharpier",
            cmd=["dotnet", "csharpier", "format" if overwrite else "check", "."],
            cwd=repo_root,
            env=env,
        )
        if exit_code != 0:
            return 1
    else:
        print("Skipped re-formatting C#.")

    if enabled(Step.REFORMAT_PYTHON):
        print("Black'ing the Python development scripts...")
        exit_code = call_and_report(
            verb="black" if overwrite else "check with black",
            cmd=[sys.executable, "-m", "black"]
            + ([] if overwrite else ["--check"])
            + PYTHON_TARGETS,
            cwd=repo_root,
        )
        if exit_code != 0:
            return 1
    else:
        print("Skipped black'ing.")

    if enabled(Step.LINT_PYTHON):
        print("Ruff'ing the Python development scripts...")
        exit_code = call_and_report(
            verb="ruff",
            cmd=[
                sys.executable,
                "-m",
                "ruff",
                "check",
                "--config",
                str(repo_root / "dev" / "continuous_integration" / "ruff.toml"),
            ]
            + (["--fix"] if overwrite else [])
            + PYTHON_TARGETS,
            cwd=repo_root,
        )
        if exit_code != 0:
            return 1
    else:
        print("Skipped ruff'ing.")

    if enabled(Step.MYPY):
        print("Mypy'ing the Python development scripts...")
        exit_code = call_and_report(
            verb="mypy",
            cmd=[
                sys.executable,
                "-m",
                "mypy",
                "--strict",
                "--ignore-missing-imports",
                "--explicit-package-bases",
            ]
            + [
                str(path.relative_to(repo_root))
                for path in sorted((repo_root / "dev").rglob("*.py"))
            ],
            cwd=repo_root,
        )
        if exit_code != 0:
            return 1
    else:
        print("Skipped mypy'ing.")

    if enabled(Step.LINT_CSHARP):
        if overwrite:
            # NOTE: ``dotnet format`` applies the fixable code-style and analyzer
            # diagnostics. It is slow, so we only run it when asked to fix.
            print("Fixing C# code-style and analyzer issues with dotnet format...")
            exit_code = call_and_report(
                verb="dotnet format",
                cmd=["dotnet", "format", "HajdeAno.slnx", "--verbosity", "quiet"],
                cwd=repo_root,
                env=env,
            )
            if exit_code != 0:
                return 1

            # dotnet format may produce code that CSharpier formats differently.
            exit_code = call_and_report(
                verb="re-format with CSharpier",
                cmd=["dotnet", "csharpier", "format", "."],
                cwd=repo_root,
                env=env,
            )
            if exit_code != 0:
                return 1

        # NOTE: The Roslyn analyzers and code-style rules (see .editorconfig and
        # Directory.Build.props) run as part of the build, which makes this the
        # fastest thorough lint available for C#.
        print("Linting C# (build with analyzers, warnings as errors)...")
        exit_code = call_and_report(
            verb="lint C#",
            cmd=[
                "dotnet",
                "build",
                "HajdeAno.slnx",
                "--configuration",
                "Debug",
                "-warnaserror",
                "-p:TreatWarningsAsErrors=true",
                "--nologo",
                "-clp:ErrorsOnly",
            ],
            cwd=repo_root,
            env=env,
        )
        if exit_code != 0:
            return 1
    else:
        print("Skipped linting C#.")

    if enabled(Step.CHECK_OFFLINE):
        print("Checking that the application uses no network or logging APIs...")
        exit_code = call_and_report(
            verb="check offline",
            cmd=[
                sys.executable,
                str(repo_root / "dev" / "continuous_integration" / "check_offline.py"),
            ],
            cwd=repo_root,
        )
        if exit_code != 0:
            return 1
    else:
        print("Skipped checking offline.")

    if enabled(Step.TEST):
        print("Testing...")
        exit_code = call_and_report(
            verb="execute unit tests",
            cmd=[
                "dotnet",
                "test",
                "HajdeAno.slnx",
                "--configuration",
                "Debug",
                "--nologo",
            ],
            cwd=repo_root,
            env=env,
        )
        if exit_code != 0:
            return 1
    else:
        print("Skipped testing.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
