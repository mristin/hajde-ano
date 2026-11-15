"""Environment for running ``dotnet`` without any telemetry."""

import os
from typing import Dict

#: Opt out of all telemetry of the .NET SDK and of Avalonia's build services.
#: (Avalonia's build telemetry is additionally disabled in Directory.Build.targets.)
TELEMETRY_OPT_OUT = {
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    "DOTNET_NOLOGO": "1",
    "AVALONIA_TELEMETRY_OPTOUT": "1",
}


def dotnet_env() -> Dict[str, str]:
    """Return a copy of the current environment with all telemetry turned off."""
    env = os.environ.copy()
    env.update(TELEMETRY_OPT_OUT)
    return env
