#!/usr/bin/env python3
"""
Render the README screenshots headlessly from synthetic data and optimize them.

The screenshots are produced by dev/screenshots (Avalonia headless + Skia) using the
fictional case tests/data/cases/06-musikschule.json; no model is needed. The PNGs are
quantized to a small palette so that they stay small in the repository (Git LFS).
"""

import argparse
import pathlib
import subprocess
import sys
import tempfile

from PIL import Image

MAX_WIDTH = 1280
COLORS = 128


def optimize(source: pathlib.Path, target: pathlib.Path) -> None:
    """Downscale (if needed) and quantize the image."""
    with Image.open(source) as opened:
        image: Image.Image = opened.convert("RGB")
    if image.width > MAX_WIDTH:
        height = round(image.height * MAX_WIDTH / image.width)
        image = image.resize((MAX_WIDTH, height), Image.Resampling.LANCZOS)
    quantized = image.quantize(
        colors=COLORS, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE
    )
    quantized.save(target, optimize=True)
    print(f"{target}: {target.stat().st_size / 1024:.0f} KiB")


def main() -> int:
    """Execute the main routine."""
    repo_root = pathlib.Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output",
        type=pathlib.Path,
        default=repo_root / "docs" / "screenshots",
        help="Where to put the optimized screenshots",
    )
    args = parser.parse_args()
    output: pathlib.Path = args.output
    output.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory() as tmp:
        raw = pathlib.Path(tmp)
        subprocess.check_call(
            [
                "dotnet",
                "run",
                "--project",
                str(repo_root / "dev" / "screenshots"),
                "--",
                str(raw),
                str(repo_root / "tests" / "data" / "cases" / "06-musikschule.json"),
            ],
            cwd=str(repo_root),
        )
        for png in sorted(raw.glob("*.png")):
            optimize(png, output / png.name)

    return 0


if __name__ == "__main__":
    sys.exit(main())
