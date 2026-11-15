#!/usr/bin/env python3
"""
Build, test or run Hajde Ano with all telemetry turned off.

Use this instead of calling ``dotnet`` directly, so that the opt-outs are never forgotten::

    python dev/build.py build            # build the solution (Debug)
    python dev/build.py test             # run all tests
    python dev/build.py run              # start the desktop application
    python dev/build.py cli -- --help    # run the developer CLI
    python dev/build.py dotnet -- ...    # any other dotnet command
"""

import argparse
import pathlib
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from dotnet_env import dotnet_env


def main() -> int:
    """Execute the main routine."""
    repo_root = pathlib.Path(__file__).resolve().parent.parent

    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("command", choices=["build", "test", "run", "cli", "dotnet"])
    parser.add_argument(
        "--configuration", default="Debug", choices=["Debug", "Release"]
    )
    parser.epilog = "Arguments after -- are passed on to dotnet."

    argv = sys.argv[1:]
    split = argv.index("--") if "--" in argv else len(argv)
    args = parser.parse_args(argv[:split])
    rest = argv[split + 1 :]
    config = ["--configuration", args.configuration]
    solution = str(repo_root / "HajdeAno.slnx")
    if args.command == "build":
        cmd = ["dotnet", "build", solution, *config, *rest]
    elif args.command == "test":
        cmd = ["dotnet", "test", solution, *config, *rest]
    elif args.command == "run":
        app = repo_root / "src" / "HajdeAno.App"
        cmd = ["dotnet", "run", "--project", str(app), *config, "--", *rest]
    elif args.command == "cli":
        cli = repo_root / "src" / "HajdeAno.Cli"
        cmd = ["dotnet", "run", "--project", str(cli), *config, "--", *rest]
    else:
        cmd = ["dotnet", *rest]

    return subprocess.call(cmd, cwd=str(repo_root), env=dotnet_env())


if __name__ == "__main__":
    sys.exit(main())
