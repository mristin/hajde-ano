#!/usr/bin/env python3
"""
Download GGUF models from the official Qwen repositories on Hugging Face.

This is a developer/installation tool. The application itself never downloads
anything. Downloads are resumable and verified against the SHA-256 published by
Hugging Face.
"""

import argparse
import dataclasses
import hashlib
import json
import pathlib
import sys
import urllib.request
from typing import Dict, List, Optional

USER_AGENT = "hajde-ano-model-downloader"


@dataclasses.dataclass(frozen=True)
class Model:
    """Describe a downloadable model."""

    key: str
    repo: str
    filename: str
    approx_size: str
    note: str


MODELS: Dict[str, Model] = {
    m.key: m
    for m in [
        Model(
            "qwen3-8b",
            "Qwen/Qwen3-8B-GGUF",
            "Qwen3-8B-Q4_K_M.gguf",
            "5.0 GB",
            "default, best quality on 16 GB RAM",
        ),
        Model(
            "qwen3-4b",
            "Qwen/Qwen3-4B-GGUF",
            "Qwen3-4B-Q4_K_M.gguf",
            "2.5 GB",
            "faster alternative for slower computers",
        ),
        Model(
            "qwen3-8b-q5",
            "Qwen/Qwen3-8B-GGUF",
            "Qwen3-8B-Q5_K_M.gguf",
            "5.9 GB",
            "higher precision, slower",
        ),
        Model(
            "qwen3-0.6b",
            "Qwen/Qwen3-0.6B-GGUF",
            "Qwen3-0.6B-Q8_0.gguf",
            "0.6 GB",
            "development only: fast smoke tests, poor recall",
        ),
    ]
}


def url_of(model: Model) -> str:
    """Return the direct download URL."""
    return f"https://huggingface.co/{model.repo}/resolve/main/{model.filename}"


def expected_sha256(model: Model) -> Optional[str]:
    """Fetch the SHA-256 of the file from the Hugging Face API, if available."""
    api = f"https://huggingface.co/api/models/{model.repo}/tree/main"
    request = urllib.request.Request(api, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            entries = json.load(response)
    except Exception as exception:  # noqa: BLE001
        print(f"Warning: could not fetch checksums: {exception}", file=sys.stderr)
        return None

    for entry in entries:
        if entry.get("path") == model.filename:
            lfs = entry.get("lfs") or {}
            oid = lfs.get("oid")
            return str(oid) if oid else None
    return None


def sha256_of(path: pathlib.Path) -> str:
    """Compute the SHA-256 of a file."""
    digest = hashlib.sha256()
    with path.open("rb") as fid:
        for block in iter(lambda: fid.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def download(model: Model, target_dir: pathlib.Path) -> int:
    """Download (or resume) the model into the target directory."""
    target_dir.mkdir(parents=True, exist_ok=True)
    target = target_dir / model.filename
    partial = target.with_suffix(target.suffix + ".part")

    sha = expected_sha256(model)

    if target.exists():
        if sha is None or sha256_of(target) == sha:
            print(f"{target} already exists.")
            return 0
        print(f"{target} exists but the checksum differs; downloading again.")
        target.unlink()

    offset = partial.stat().st_size if partial.exists() else 0
    headers = {"User-Agent": USER_AGENT}
    if offset > 0:
        headers["Range"] = f"bytes={offset}-"
        print(f"Resuming at {offset / 1e9:.2f} GB")

    request = urllib.request.Request(url_of(model), headers=headers)
    print(f"Downloading {model.filename} (~{model.approx_size}) from {model.repo}")
    with urllib.request.urlopen(request, timeout=60) as response:
        if offset > 0 and response.status != 206:
            offset = 0  # Server ignored the range; start over.
        total = response.length + offset if response.length else None
        mode = "ab" if offset > 0 else "wb"
        done = offset
        with partial.open(mode) as fid:
            while True:
                block = response.read(1 << 20)
                if not block:
                    break
                fid.write(block)
                done += len(block)
                if total:
                    print(
                        f"\r  {done / 1e9:6.2f} / {total / 1e9:.2f} GB "
                        f"({100.0 * done / total:5.1f} %)",
                        end="",
                        flush=True,
                    )
    print()

    if sha is not None:
        print("Verifying SHA-256...")
        actual = sha256_of(partial)
        if actual != sha:
            print(
                f"Checksum mismatch: expected {sha}, got {actual}. "
                f"Delete {partial} and try again.",
                file=sys.stderr,
            )
            return 1

    partial.rename(target)
    print(f"Saved to {target}")
    return 0


def main() -> int:
    """Execute the main routine."""
    repo_root = pathlib.Path(__file__).resolve().parent.parent

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "models",
        nargs="*",
        default=["qwen3-8b"],
        help="Models to download (default: qwen3-8b). Use --list to see all.",
    )
    parser.add_argument(
        "--target",
        type=pathlib.Path,
        default=repo_root / "models",
        help="Target directory (default: models/ in the repository)",
    )
    parser.add_argument("--list", action="store_true", help="List the models and exit")
    args = parser.parse_args()

    if args.list:
        for model in MODELS.values():
            print(
                f"{model.key:12} {model.filename:24} ~{model.approx_size:7} {model.note}"
            )
            print(f"{'':12} {url_of(model)}")
        return 0

    unknown: List[str] = [key for key in args.models if key not in MODELS]
    if unknown:
        print(
            f"Unknown model(s): {', '.join(unknown)}; choose from "
            f"{', '.join(MODELS)}",
            file=sys.stderr,
        )
        return 1

    for key in args.models:
        exit_code = download(MODELS[key], args.target)
        if exit_code != 0:
            return exit_code

    return 0


if __name__ == "__main__":
    sys.exit(main())
