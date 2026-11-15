#!/usr/bin/env python3
"""
Publish and package the self-contained desktop application.

Produces, in the output directory:

* ``HajdeAno-<version>-win-x64.zip`` (and ``win-arm64``): portable folder,
* ``HajdeAno-<version>-osx-arm64.zip`` (and ``osx-x64``): zipped ``Hajde Ano.app`` bundle,
* ``HajdeAno-<version>-linux-x64.tar.gz``: portable folder.

The model weights are *not* included; see the README for download instructions.
"""

import argparse
import os
import pathlib
import platform
import plistlib
import shutil
import subprocess
import sys
import tarfile
import zipfile
from typing import List

APP_NAME = "Hajde Ano"
EXE_NAME = "HajdeAno"
BUNDLE_ID = "ch.ristin.hajde-ano"

RIDS = ["win-x64", "win-arm64", "osx-arm64", "osx-x64", "linux-x64"]

MODELS_README = """\
Put the GGUF model files into this folder, for example:

    Qwen3-8B-Q4_K_M.gguf   (~5.0 GB, default, recommended for 16 GB RAM)
    https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf

    Qwen3-4B-Q4_K_M.gguf   (~2.5 GB, faster alternative for slower computers)
    https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf

The application never downloads anything by itself. You can also select a model
anywhere on disk in Settings.
"""


def native_rid() -> str:
    """Guess the runtime identifier of this machine."""
    machine = platform.machine().lower()
    arch = "arm64" if machine in ("arm64", "aarch64") else "x64"
    if sys.platform == "win32":
        return f"win-{arch}"
    if sys.platform == "darwin":
        return f"osx-{arch}"
    return f"linux-{arch}"


def run(cmd: List[str], cwd: pathlib.Path) -> None:
    """Run the command and fail loudly."""
    print("Executing:", " ".join(cmd), flush=True)
    subprocess.check_call(cmd, cwd=str(cwd))


def dotnet_publish(
    repo_root: pathlib.Path, rid: str, version: str, target: pathlib.Path
) -> None:
    """Publish a self-contained single-file executable for the runtime identifier."""
    if target.exists():
        shutil.rmtree(target)
    run(
        [
            "dotnet",
            "publish",
            str(repo_root / "src" / "HajdeAno.App" / "HajdeAno.App.csproj"),
            "--configuration",
            "Release",
            "--runtime",
            rid,
            "--self-contained",
            "true",
            "--output",
            str(target),
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:PublishTrimmed=false",
            "-p:DebugType=none",
            "-p:GenerateDocumentationFile=false",
            f"-p:Version={version}",
        ],
        cwd=repo_root,
    )

    # NOTE: The llama.cpp libraries of LLamaSharp are content files, not native assets,
    # and therefore stay next to the executable in runtimes/<rid>/native/. LLamaSharp
    # loads them from there; this is the "distribute alongside" option of DESIGN.md.
    natives = target / "runtimes"
    if not natives.exists():
        raise RuntimeError(f"Expected the native llama.cpp libraries in {natives}")

    for pdb in target.rglob("*.pdb"):
        pdb.unlink()


def add_docs(repo_root: pathlib.Path, folder: pathlib.Path) -> None:
    """Add license and model instructions."""
    shutil.copy(repo_root / "LICENSE", folder / "LICENSE.txt")
    models = folder / "models"
    models.mkdir(exist_ok=True)
    (models / "README.txt").write_text(MODELS_README, encoding="utf-8")


def zip_folder(source: pathlib.Path, archive: pathlib.Path) -> None:
    """Zip the folder (including the folder itself), preserving executable bits."""
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zf:
        for path in sorted(source.rglob("*")):
            arcname = path.relative_to(source.parent).as_posix()
            if path.is_dir():
                continue
            info = zipfile.ZipInfo.from_file(path, arcname)
            info.compress_type = zipfile.ZIP_DEFLATED
            with path.open("rb") as fid:
                zf.writestr(info, fid.read())


def package_portable(
    repo_root: pathlib.Path,
    publish_dir: pathlib.Path,
    rid: str,
    version: str,
    out: pathlib.Path,
) -> pathlib.Path:
    """Package Windows (zip) or Linux (tar.gz) as a portable folder."""
    staging = out / "staging" / rid / EXE_NAME
    if staging.parent.exists():
        shutil.rmtree(staging.parent)
    shutil.copytree(publish_dir, staging)
    add_docs(repo_root, staging)

    if rid.startswith("win"):
        archive = out / f"{EXE_NAME}-{version}-{rid}.zip"
        zip_folder(staging, archive)
    else:
        archive = out / f"{EXE_NAME}-{version}-{rid}.tar.gz"
        with tarfile.open(archive, "w:gz") as tf:
            tf.add(staging, arcname=EXE_NAME)
    return archive


def package_macos(
    repo_root: pathlib.Path,
    publish_dir: pathlib.Path,
    rid: str,
    version: str,
    out: pathlib.Path,
) -> pathlib.Path:
    """Build the .app bundle, ad-hoc sign it on macOS and zip it."""
    staging = out / "staging" / rid
    if staging.exists():
        shutil.rmtree(staging)
    bundle = staging / f"{APP_NAME}.app"
    contents = bundle / "Contents"
    macos = contents / "MacOS"
    resources = contents / "Resources"
    resources.mkdir(parents=True)

    # The executable and the llama.cpp libraries (runtimes/) live in MacOS/ so that
    # LLamaSharp finds them next to the executable.
    shutil.copytree(publish_dir, macos)
    exe = macos / EXE_NAME
    exe.chmod(0o755)

    shutil.copy(
        repo_root / "dev" / "assets" / "hajde-ano.icns", resources / "hajde-ano.icns"
    )
    add_docs(repo_root, resources)

    short_version = version.split("-")[0]
    info = {
        "CFBundleName": APP_NAME,
        "CFBundleDisplayName": APP_NAME,
        "CFBundleIdentifier": BUNDLE_ID,
        "CFBundleExecutable": EXE_NAME,
        "CFBundleIconFile": "hajde-ano.icns",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": short_version,
        "CFBundleVersion": short_version,
        "LSMinimumSystemVersion": "12.0",
        "NSHighResolutionCapable": True,
        "LSApplicationCategoryType": "public.app-category.productivity",
    }
    with (contents / "Info.plist").open("wb") as fid:
        plistlib.dump(info, fid)

    archive = out / f"{EXE_NAME}-{version}-{rid}.zip"
    if sys.platform == "darwin":
        # Apple Silicon refuses to run unsigned code; an ad-hoc signature is the minimum.
        # Proper distribution requires a Developer ID signature and notarization.
        identity = os.environ.get("MACOS_SIGN_IDENTITY", "-")
        run(
            [
                "codesign",
                "--force",
                "--deep",
                "--options",
                "runtime",
                "--sign",
                identity,
                str(bundle),
            ],
            cwd=staging,
        )
        run(
            ["ditto", "-c", "-k", "--keepParent", bundle.name, str(archive)],
            cwd=staging,
        )
    else:
        print(
            "Warning: not on macOS; the bundle is not signed and will not start on "
            "Apple Silicon without signing.",
            file=sys.stderr,
        )
        zip_folder(bundle, archive)
    return archive


def main() -> int:
    """Execute the main routine."""
    repo_root = pathlib.Path(__file__).resolve().parent.parent

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--rid",
        nargs="+",
        choices=RIDS,
        default=[native_rid()],
        help="Runtime identifiers to publish (default: this machine)",
    )
    parser.add_argument("--version", default="0.0.0-dev", help="Version to stamp")
    parser.add_argument(
        "--output",
        type=pathlib.Path,
        default=repo_root / "artifacts",
        help="Output directory",
    )
    parser.add_argument(
        "--self-test",
        help=(
            "After publishing for this machine, run the published executable "
            "on this GGUF model to verify native library loading"
        ),
    )
    args = parser.parse_args()

    version = str(args.version).removeprefix("v")
    out: pathlib.Path = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)

    for rid in args.rid:
        publish_dir = out / "publish" / rid
        dotnet_publish(repo_root, rid, version, publish_dir)

        if args.self_test and rid == native_rid():
            exe = publish_dir / (EXE_NAME + (".exe" if rid.startswith("win") else ""))
            run(
                [str(exe), "--self-test", str(pathlib.Path(args.self_test).resolve())]
                + (
                    ["--no-template"]
                    if "stories" in pathlib.Path(args.self_test).name
                    else []
                ),
                cwd=out,
            )

        if rid.startswith("osx"):
            archive = package_macos(repo_root, publish_dir, rid, version, out)
        else:
            archive = package_portable(repo_root, publish_dir, rid, version, out)
        print(f"Packaged {archive} ({archive.stat().st_size / 1e6:.1f} MB)")

    shutil.rmtree(out / "staging", ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
