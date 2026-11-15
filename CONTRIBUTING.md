# Contributing

Thank you for helping to improve Hajde Ano!

> **Never use real case descriptions while developing or testing,** neither in this
> repository nor in conversations with online services (ChatGPT, Claude, …). Only use
> synthetic data such as the cases in `tests/data/cases/`. Files like `example-case.txt`
> are git-ignored for a reason.

## Repository layout

```
src/
  HajdeAno.Core/     Detection (regex, LLM orchestration, merging), placeholders,
                     case dictionary, restoration, encryption, evaluation
  HajdeAno.Llm/      In-process inference through LLamaSharp (llama.cpp)
  HajdeAno.App/      Avalonia desktop application (MVVM)
  HajdeAno.Cli/      Developer CLI: extract, anonymize, restore, evaluate
tests/
  HajdeAno.Core.Tests/  Unit tests (no model needed)
  HajdeAno.Llm.Tests/   Integration tests with a 1 MB toy model (real llama.cpp)
  data/cases/           Synthetic, annotated case descriptions
  data/models/          stories260K.gguf toy model (Git LFS)
dev/
  continuous_integration/  precommit.py, check_offline.py, ruff.toml
  screenshots/             Headless renderer for the README screenshots
  assets/                  Icon generator
  build.py                 Build, test and run with all telemetry turned off
  dotnet_env.py            The telemetry opt-outs shared by all dev scripts
  download_models.py       Download GGUF models (verified by SHA-256)
  publish.py               Publish and package for Windows, macOS and Linux
  make_screenshots.py      Render and optimize the README screenshots
docs/                      Privacy architecture, benchmarks, screenshots
```

See [DESIGN.md](DESIGN.md) for the original requirements.

## Prerequisites

* [.NET SDK 10](https://dotnet.microsoft.com/download) (see `global.json`)
* Python 3.10+ for the development scripts:
  `python3 -m pip install -r dev/requirements.txt`
* [Git LFS](https://git-lfs.com/) for images and the toy model. After cloning, run
  `git lfs install && git lfs pull`.

## Build and run

```sh
python3 dev/build.py build
python3 dev/download_models.py            # Qwen3-8B, ~5 GB (or: qwen3-4b, qwen3-0.6b)
python3 dev/build.py run
```

### No telemetry, also not while building

Avalonia pulls in `Avalonia.BuildServices`, which sends anonymous **build** statistics to
Avalonia's servers unless `AVALONIA_TELEMETRY_OPTOUT=1` is set; the .NET SDK has its own
telemetry (`DOTNET_CLI_TELEMETRY_OPTOUT=1`). Neither is part of the application, but we
turn both off everywhere, so that it can never be forgotten:

* `Directory.Build.targets` replaces Avalonia's `AvaloniaStats` target with an empty one,
  so that **every** build (command line, IDE, CI) skips it, even without the environment
  variable. `check-offline` fails if this override is removed.
* `dev/build.py` (build, test, run, cli or any other `dotnet` command), `precommit.py`,
  `publish.py` and `make_screenshots.py` set both opt-outs (`dev/dotnet_env.py`), and so
  do the GitHub workflows.

Prefer `python3 dev/build.py ...` over calling `dotnet` directly. If you do call `dotnet`
yourself (or use an IDE), export the variables in your shell profile as well:

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1
```

For quick experiments `python3 dev/download_models.py qwen3-0.6b` fetches a 0.6 GB model.
It is fast but misses far more entities. Don't draw quality conclusions from it.

Set `HAJDE_ANO_DATA_DIR` to use a separate vault and settings folder during development.

## Pre-commit checks

Run all checks before committing:

```sh
python3 dev/continuous_integration/precommit.py
```

To let the tools fix what they can:

```sh
python3 dev/continuous_integration/precommit.py --overwrite
```

The steps (select them with `--select` or skip them with `--skip`):

| Step | C# | Python |
|---|---|---|
| `reformat-csharp` / `reformat-python` | [CSharpier](https://csharpier.com/) (dotnet local tool) | [black](https://black.readthedocs.io/) |
| `lint-csharp` / `lint-python` | Roslyn analyzers and code style in the build (`-warnaserror`); with `--overwrite`, `dotnet format` applies fixes first | [ruff](https://docs.astral.sh/ruff/) (`--fix` with `--overwrite`) |
| `mypy` | | mypy `--strict` |
| `check-offline` | Fails if `src/` uses network, process, telemetry or logging APIs | |
| `test` | `dotnet test` (unit tests and toy-model integration tests) | |

### Is there a fast linter for C#?

There is no single standalone tool like ruff for C#. The fastest setup we found:

* **Formatting: CSharpier.** It's opinionated and very fast (about 1–2 s for the whole
  repository), similar to black.
* **Linting: the Roslyn analyzers that ship with the .NET SDK,** enabled via
  `AnalysisLevel` and `EnforceCodeStyleInBuild` in `Directory.Build.props` and tuned in
  `.editorconfig`. They run as part of an incremental `dotnet build`, so linting costs
  only a few seconds beyond the build itself.
* `dotnet format` can apply many fixes automatically, but it reloads the whole solution
  and is slow, so the pre-commit script only runs it with `--overwrite`.
* JetBrains InspectCode and SonarAnalyzer are more thorough but considerably slower.

## Tests

```sh
python3 dev/build.py test
```

* `HajdeAno.Core.Tests` cover regex detection, overlapping spans, placeholder
  consistency across documents, exact round-trip restoration, invalid and truncated LLM
  JSON, hallucinated entities, unknown and malformed placeholders, chunking of long
  documents, restoring Word documents (placeholders split over formatting runs,
  headers, footers, notes, title), encryption (wrong password, tampering, no plaintext on disk, hashed file
  names, rolled-back and deleted cases, damaged vault files, migration of old vaults) and
  the synthetic cases with an oracle detector.
* `HajdeAno.Llm.Tests` run the real llama.cpp inference path with a 1 MB toy model
  (nonsense output, but native loading, grammar-constrained sampling and validation are
  exercised on every OS in CI).
* `HajdeAno.App.Tests` drive the real window and view models headlessly (Avalonia
  Headless): the text survives closing and restarting the program, each case keeps its
  own text, deleting a case deletes its text, New/Open ask before replacing text,
  autosave only writes changed text, a detection result can never end up in another
  case, selecting text with the mouse and clicking "Anonymize selection" works,
  clicking the disabled copy button explains why, and answers are restored from Word
  and text files.

### Evaluating detection quality

```sh
python3 dev/build.py cli --configuration Release -- evaluate --model models/Qwen3-8B-Q4_K_M.gguf --report report.md
```

This measures recall per category on `tests/data/cases/`, plus run time and peak memory.
Add new synthetic cases as JSON files there. Each `expected` entry must occur verbatim in
a document (a unit test checks this). Record notable results in
[docs/benchmarks.md](docs/benchmarks.md).

## Screenshots

```sh
python3 dev/make_screenshots.py
```

This renders the real views headlessly (Avalonia + Skia) with the fictional case
`tests/data/cases/06-musikschule.json` and a scripted detector. It needs no model. The
images are quantized to keep them small and are stored with Git LFS.

## Publishing

```sh
python3 dev/publish.py --rid win-x64 osx-arm64 linux-x64 --version 1.2.3 \
  --self-test tests/data/models/stories260K.gguf
```

* Publishes self-contained, single-file executables (`PublishSingleFile`,
  `IncludeNativeLibrariesForSelfExtract`, no trimming).
* The llama.cpp libraries of LLamaSharp stay next to the executable in
  `runtimes/<rid>/native/`, because LLamaSharp probes for them on disk. A target in
  `HajdeAno.App.csproj` keeps only those of the target platform.
* `--self-test` runs the *published* executable on the given model. This verifies that
  the native libraries load from the package.
* macOS bundles (`Hajde Ano.app`) are ad-hoc signed when built on macOS. For notarized
  distribution, set `MACOS_SIGN_IDENTITY` to a Developer ID identity in the keychain and
  notarize the zip with `xcrun notarytool` (not automated yet).

### Releases

Create a release on GitHub (e.g. tag `v0.1.0`). The
[release workflow](.github/workflows/release.yml) builds, tests and self-tests the
packages for Linux (x64), Windows (x64, ARM64) and macOS (ARM64, x64) and attaches them to
the release. The model weights are never packaged.

## Coding guidelines

* No network, telemetry, logging, crash reporting or plaintext temporary files in `src/`
  (enforced by `check-offline`). Never put private text into exception messages or UI
  error messages. Unexpected errors show only their type; the crash handler in
  `Program.cs` exits without printing details or writing a core dump.
* Everything private is written only through `Vault` (encrypted, atomic). Never write
  case data, mappings or drafts unencrypted, not even in the CLI.
* Keep all text replacement deterministic and in C#. The model only *reports* spans.
* Prefer recall over precision. Uncertain detections are flagged for review, not
  dropped.
