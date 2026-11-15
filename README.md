# Hajde Ano

**Anonymize private case descriptions locally before you paste them into ChatGPT, and
restore the names afterwards. Fully offline.**

Hajde Ano is a desktop application for Windows, macOS and Linux. It finds names,
organizations, addresses, IBANs, case numbers and other private information in German
(Swiss High German) texts, using a local language model (Qwen3 via llama.cpp) together
with deterministic patterns. It replaces them with consistent placeholders such as
`[PERSON_1]` or `[ORG_2_ABBR]`. You then send only the reviewed, anonymized text to an
online AI service. When the answer comes back, Hajde Ano puts the original values back in.

No private text, name or mapping ever leaves your computer.

![Review: original and anonymized text side by side, with every detection highlighted](docs/screenshots/review.png)

![Restore: placeholders in the AI's answer are replaced by the original values; modified placeholders are flagged](docs/screenshots/restore.png)

## How it works

1. **Cases.** Create a case, for example one per client. Each case has its own encrypted
   dictionary of entities, so the same person gets the same placeholder in every document
   of that case.
2. **Input.** Paste the text or open a `.txt`/`.md` file, then click *Detect private
   information*.
3. **Detection** runs completely on your computer:
   * regular expressions for structured data (e-mail, phone, IBAN with checksum, AHV
     number, dates, amounts, Swiss addresses, debt-collection and court references),
   * the local Qwen3 model for contextual information (names, organizations, authorities,
     courts, nationalities, …), constrained to output JSON,
   * the case dictionary, including fuzzy matching of misspelled names,
   * a second model pass over the anonymized text to catch leftovers.

   Every span the model reports is validated against the original text, and invented
   spans are discarded. All replacements are done deterministically by the application,
   never by the model.
4. **Review.** Original and anonymized text appear side by side. Uncertain detections
   are highlighted in orange and must be decided: anonymize, keep, or change the
   category. You can select any text to anonymize it manually. Copying is only possible
   after you confirm the review.
5. **Restore.** Paste the AI's answer. Known placeholders are replaced by the original
   values. Unknown or modified placeholders (e.g. `[PERSON 1]`) are reported, never
   guessed.

Placeholders are stable and human-readable:

| Original | Placeholder |
|---|---|
| Anna Müller | `[PERSON_1]` |
| Anna | `[PERSON_1_FIRST]` |
| Frau Müller | `Frau [PERSON_1_LAST]` |
| Müller (shared by several persons) | `[FAMILY_1]` (flagged for review) |
| Stiftung Beispiel / Beispiel | `[ORG_1]` / `[ORG_1_SHORT]` |
| Sozialversicherungsanstalt (SVA) | `[AGENCY_1]` (`[AGENCY_1_ABBR]`) |
| CHF 4'500.– | `[AMOUNT_1]` |
| Betreibung Nr. 2024-118734 | `Betreibung Nr. [CASE_ID_1]` |

In *strict privacy mode* (the default), indirect identifiers such as dates, amounts,
nationalities and places are replaced as well.

## Installation

1. Download the package for your system from the
   [releases](https://github.com/mristin/hajde-ano/releases):
   * Windows: `HajdeAno-<version>-win-x64.zip` (or `win-arm64`). Unzip and run
     `HajdeAno.exe`.
   * macOS: `HajdeAno-<version>-osx-arm64.zip` (Apple Silicon) or `osx-x64` (Intel). Unzip
     and move `Hajde Ano.app` to *Applications*.
   * Linux: `HajdeAno-<version>-linux-x64.tar.gz`. Extract and run `HajdeAno/HajdeAno`.

   The packages are self-contained: you do **not** need .NET, Python, llama.cpp or Ollama.

2. Download a model (see below) and put it into the `models/` folder next to the
   application, or select it anywhere on disk under *Settings*.

### Downloading a model

The model weights are several gigabytes and are **not** included in the release
packages. The application never downloads anything by itself. Download one of these GGUF
files from the official Qwen repositories on Hugging Face (Apache 2.0 license):

| Model | Size | When to use | Download |
|---|---:|---|---|
| **Qwen3-8B-Q4_K_M** | 5.0 GB | Default, best recall; 16 GB RAM | [Qwen3-8B-Q4_K_M.gguf](https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf) |
| Qwen3-4B-Q4_K_M | 2.5 GB | Faster, for slower computers | [Qwen3-4B-Q4_K_M.gguf](https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf) |
| Qwen3-8B-Q5_K_M | 5.9 GB | Higher precision, slower | [Qwen3-8B-Q5_K_M.gguf](https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q5_K_M.gguf) |

Repositories: [Qwen/Qwen3-8B-GGUF](https://huggingface.co/Qwen/Qwen3-8B-GGUF),
[Qwen/Qwen3-4B-GGUF](https://huggingface.co/Qwen/Qwen3-4B-GGUF).

Where the application looks for models:

* the path selected in *Settings*,
* `models/` next to the executable (Windows, Linux),
* `Hajde Ano.app/Contents/Resources/models/` (macOS),
* `models/` in the application data folder (shown in *Settings*).

If you have a clone of this repository, `python3 dev/download_models.py` downloads the
default model into `models/` and verifies its SHA-256 (`--list` shows all options).

### macOS notes

The macOS packages are ad-hoc signed but not notarized. On first start, macOS may refuse
to open the app. Right-click the app and choose *Open*, or run:

```sh
xattr -dr com.apple.quarantine "/Applications/Hajde Ano.app"
```

macOS 12 or newer is required. On Apple Silicon you can enable *Use GPU* in *Settings*
for Metal acceleration. Intel Macs run CPU inference, which is slow for the 8B model, so
prefer Qwen3-4B there.

## Hardware

The default model needs a computer with **16 GB RAM**. No GPU is required. The 8B model
uses about 5 GB for the weights plus 1–2 GB for inference. Detection of a one-page
document takes roughly half a minute to a few minutes on a laptop CPU (see
[docs/benchmarks.md](docs/benchmarks.md)). Only one model is loaded at a time, and only
one inference runs at a time.

## Privacy

* No network access at all: no cloud APIs, no telemetry, no update checks, no model
  downloads, no local HTTP server, no external processes. The application works inside
  an OS-level network sandbox (e.g. with the network disabled).
* Cases and mappings are encrypted at rest with AES-256-GCM, using a key derived from
  your password (PBKDF2-HMAC-SHA256, 600 000 iterations). The key is only kept in memory.
* No logging of texts, names or mappings, and no plaintext temporary files.
* Only the text you approved on the *Review* screen is ever exported (clipboard).

See [docs/privacy-architecture.md](docs/privacy-architecture.md) for details.

> **A language model cannot guarantee complete anonymization.** Hajde Ano is tuned for
> recall (it prefers to flag too much over missing a name), but you must always read
> the anonymized text before you share it.

## Command-line tool

For development and evaluation there is also a CLI (`src/HajdeAno.Cli`). Use it only with
synthetic data:

```sh
dotnet run --project src/HajdeAno.Cli -- extract --model models/Qwen3-8B-Q4_K_M.gguf case.txt
dotnet run --project src/HajdeAno.Cli -- anonymize --model models/Qwen3-8B-Q4_K_M.gguf --save-dictionary dict.json case.txt
dotnet run --project src/HajdeAno.Cli -- restore --dictionary dict.json answer.txt
dotnet run --project src/HajdeAno.Cli -- evaluate --model models/Qwen3-8B-Q4_K_M.gguf
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for building from source, tests, the pre-commit
script and releases.

## License

MIT, see [LICENSE](LICENSE). The Qwen3 models are licensed under Apache 2.0.
llama.cpp (via [LLamaSharp](https://github.com/SciSharp/LLamaSharp)) and
[Avalonia](https://avaloniaui.net/) are MIT-licensed.
