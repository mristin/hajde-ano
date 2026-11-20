# Hajde Ano — Implementation Brief

## Goal

Build a completely offline desktop application that automatically anonymizes private case descriptions before they are sent to ChatGPT or other online AI services.

The application must detect personal names, company names, organizations, addresses, legal identifiers and other sensitive information, and replace them with consistent placeholders.

The user can then copy the anonymized text into ChatGPT to generate letters, summaries, translations, etc. After receiving the response, the user pastes it back into the local application, which restores the original names and other information.

No private information, original text or placeholder mappings must ever leave the local computer.

## Technology Stack

- C# / .NET 10
- Avalonia UI for cross-platform desktop GUI
- LLamaSharp for in-process local LLM inference
- LLamaSharp.Backend.Cpu for native inference libraries
- Qwen3 8B, quantized to Q4_K_M, as the primary model
- Qwen3 4B Q4_K_M as an optional faster alternative
- System.Text.RegularExpressions for deterministic pattern detection
- System.Text.Json for structured LLM output and serialization
- Local storage for case dictionaries and placeholder mappings in a session folder chosen
  by the user (unencrypted; rely on full-disk encryption)

The application must run on computers with 16 GB RAM, without a dedicated GPU.

No separate installation of llama.cpp, Ollama, Python or .NET runtime should be required. LLamaSharp must bundle the native inference libraries with the application.

## Model Downloads

Primary model:

Qwen3-8B-Q4_K_M.gguf (~5.03 GB)

Model repository:
https://huggingface.co/Qwen/Qwen3-8B-GGUF

Direct download:
https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf

Optional smaller and faster model:

Qwen3-4B-Q4_K_M.gguf (~2.50 GB)

Model repository:
https://huggingface.co/Qwen/Qwen3-4B-GGUF

Direct download:
https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf

Optional higher-precision model:

Qwen3-8B-Q5_K_M.gguf (~5.85 GB)

Direct download:
https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q5_K_M.gguf

All these models are distributed under the Apache 2.0 license.

Store models in a local models/ directory. Do not commit model weights to Git. Provide a separate download script for developers.

Verify the exact filenames against the official model repositories when implementing the download script.

## Dependencies

LLamaSharp:

https://github.com/SciSharp/LLamaSharp

https://www.nuget.org/packages/LLamaSharp

CPU backend:

https://www.nuget.org/packages/LLamaSharp.Backend.Cpu

Avalonia:

https://docs.avaloniaui.net/

Use LLamaSharp 0.30.0 as the initial candidate version, or a newer compatible stable release. Verify the API and native backend availability before implementation.

## Local LLM Configuration

Start with Qwen3 8B Q4_K_M.

Configuration:

- Context size: 4096 tokens initially
- Optional context size: 8192 tokens
- Temperature: 0 or 0.1
- Disable Qwen3 thinking mode
- CPU inference: GpuLayerCount = 0
- Apple Silicon: try Metal acceleration with GpuLayerCount = 99
- Use constrained JSON generation if supported by LLamaSharp

Use Qwen3's correct chat template and non-thinking mode.

The LLM should NOT rewrite the original text. It should only detect private information and return structured JSON containing the exact sensitive text spans.

Example input:

Anna Müller wohnt in Zürich. Die Stiftung Beispiel hat ihr eine Wohnung vermittelt. Die Verwaltung verlangt CHF 4500 für die Räumung.

Example LLM output:

[
  {"text": "Anna Müller", "type": "PERSON"},
  {"text": "Zürich", "type": "CITY"},
  {"text": "Stiftung Beispiel", "type": "ORGANIZATION"},
  {"text": "CHF 4500", "type": "AMOUNT"}
]

The C# application must validate that each detected span actually occurs in the original text.

Perform all text replacements deterministically in C#, not through the LLM.

## Anonymization Categories

Detect at least the following categories:

- Full personal names
- First names
- Surnames
- Family names
- Company names
- Organization names
- Foundations
- Housing cooperatives
- Organization abbreviations
- Government agencies
- Courts
- Street addresses
- Cities and municipalities
- Postal codes
- Countries
- Nationalities
- Phone numbers
- Email addresses
- Websites and URLs
- Dates
- Dates of birth
- Financial amounts
- Bank account numbers and IBANs
- Legal case numbers
- Debt collection references
- Customer numbers
- Social security numbers
- Immigration permit numbers
- Other identifying information

The primary language is German, particularly Swiss High German. However, personal names may originate from many different languages.

Support spelling mistakes, alternative spellings, abbreviations and names appearing in different forms.

## Anonymization Pipeline

1. Load the original text locally.

2. Run regular expressions to detect structured sensitive information such as email addresses, phone numbers, dates, amounts, IBANs and identifiers.

3. Run Qwen3 locally to detect contextual private information, particularly personal names, organizations, addresses and indirect identifiers.

4. Match detections against the local case dictionary.

5. Merge detections from all sources and resolve overlapping spans.

6. Create consistent placeholders.

7. Replace sensitive spans deterministically.

8. Run a second LLM detection pass on the anonymized text to detect remaining sensitive information.

9. Present the original and anonymized text side by side for human review.

10. Allow the user to manually select additional text to anonymize.

11. Export only the approved anonymized text.

For long documents, split the text into overlapping chunks and reconcile entities across chunks. Do not silently truncate the original document.

## Placeholder System

Use stable, human-readable placeholders.

Examples:

[PERSON_1]
[PERSON_1_FIRST]
[PERSON_1_LAST]
[FAMILY_1]
[ORG_1]
[ORG_1_SHORT]
[ORG_1_ABBR]
[CITY_1]
[ADDRESS_1]
[COUNTRY_1]
[DATE_1]
[AMOUNT_1]
[CASE_ID_1]
[PHONE_1]
[EMAIL_1]

The same entity must receive the same placeholder throughout a case and across multiple documents belonging to that case.

For example:

Anna Müller -> [PERSON_1]
Anna -> [PERSON_1_FIRST]
Frau Müller -> Frau [PERSON_1_LAST]
Stiftung Beispiel -> [ORG_1]
Beispiel -> [ORG_1_SHORT]

Keep family names separate from individual identities, since multiple people can share a surname.

Do not automatically assume that every occurrence of a common word or surname refers to the same entity. Flag ambiguous matches for review.

## Local Case Dictionary

Each case must have its own persistent entity dictionary.

Example:

{
  "PERSON_1": {
    "type": "PERSON",
    "full_name": "Anna Müller",
    "first_name": "Anna",
    "last_name": "Müller",
    "family": "FAMILY_1"
  },
  "ORG_1": {
    "type": "ORGANIZATION",
    "full_name": "Stiftung Beispiel",
    "short_name": "Beispiel"
  }
}

Store this information only locally, in the session folder chosen by the user.

The user should be able to:

- Create a new case
- Open an existing case
- Add or edit entities manually
- Add aliases and abbreviations
- Reuse mappings across documents
- Delete a case and its stored mappings

## Restoration

The user pastes a letter or other text generated by ChatGPT into the application.

The application restores placeholders using the local case dictionary.

Example:

Input:

Sehr geehrte Damen und Herren von [ORG_1],

Hiermit bestreite ich die Forderung von [AMOUNT_1] im Zusammenhang mit der Betreibung [CASE_ID_1].

Freundliche Grüsse
[PERSON_1]

The application replaces all known placeholders with their original values.

Restoration must be deterministic and must not use an LLM.

Detect unknown, malformed or modified placeholders and warn the user rather than guessing their values.

Show the restored text for review before allowing it to be copied.

## GUI

Use Avalonia UI.

Create a simple desktop application with the following screens:

1. Cases

List existing cases, create new cases, open cases and delete cases.

2. Input

Paste original text or open a local TXT or Markdown file.

Button: Detect private information.

3. Review

Display original and anonymized text side by side.

Highlight every detected private entity.

Show a list of detected entities, their categories, aliases and placeholders.

Allow users to approve, reject, edit or add detections.

Allow users to select any text span manually and assign it a placeholder.

Button: Copy anonymized text.

4. Restore

Paste the response received from ChatGPT.

Display the restored text.

Warn about unresolved placeholders.

Button: Copy restored text.

5. Settings

Select the local GGUF model.

Select CPU or GPU inference.

Configure context size.

Configure sensitive information categories.

Configure strict privacy mode, which also replaces dates, amounts, nationalities, locations and other indirect identifiers.

## Offline and Security Requirements

These are mandatory:

- The application must operate without Internet access.
- No cloud APIs.
- No telemetry.
- No analytics.
- No automatic update checks.
- No automatic model downloads during application execution.
- No external inference servers.
- No localhost HTTP server.
- No Ollama process.
- No automatic cloud fallback.
- No network requests during anonymization or restoration.
- No logging of private texts, names or mappings.
- No plaintext sensitive data in temporary files.
- No crash reports containing private information.
- Store persistent case dictionaries and mappings only in the session folder chosen by
  the user, and tell the user that they are not encrypted.
- If the model is missing, show a local error instead of downloading anything.
- Only the explicitly approved anonymized text may be exported.

The application should be usable inside an operating-system-level network sandbox.

Separate model downloads and dependency installation from normal application execution.

Do not send real private case descriptions to Claude Code, ChatGPT or any other online service while developing or testing this application. Use synthetic case descriptions instead.

## Distribution

Publish self-contained .NET executables.

Target platforms:

- Windows x64
- macOS ARM64 (Apple Silicon)
- macOS x64 (Intel)

Optionally support Windows ARM64 if compatible native inference binaries are available.

Use the following .NET publish settings:

SelfContained = true
PublishSingleFile = true
IncludeNativeLibrariesForSelfExtract = true
PublishTrimmed = false

Test LLamaSharp native library loading after publishing. If native libraries cannot reliably be loaded from a single-file executable, distribute them alongside the executable.

Keep GGUF model files separate from the executable so that they can be memory-mapped efficiently.

Windows distribution:

HajdeAno/
  HajdeAno.exe
  models/
    Qwen3-8B-Q4_K_M.gguf
  runtimes/win-x64/native/ (llama.cpp libraries)

macOS distribution:

Hajde Ano.app/
  Contents/
    MacOS/
      HajdeAno
      runtimes/osx-arm64/native/ (llama.cpp libraries)
    Resources/
      models/
        Qwen3-8B-Q4_K_M.gguf

The user must not need to install .NET, llama.cpp, Ollama or Python.

Provide a portable ZIP for Windows and a macOS application bundle.

Verify macOS minimum OS requirements, native library compatibility, code signing and notarization requirements. Do not assume older Intel Macs are supported merely because an osx-x64 build exists.

## Hardware Requirements

Target computers with 16 GB RAM.

Expected memory usage for Qwen3 8B Q4_K_M:

- Model weights: approximately 5 GB
- Context and inference buffers: approximately 1–2 GB
- Application overhead: approximately 200–500 MB
- Operating system: approximately 2–4 GB

These are estimates. Measure actual peak memory consumption during testing.

Run one LLM inference operation at a time.

Do not load both the 4B and 8B models simultaneously.

Provide an easy way to switch to Qwen3 4B for slower computers.

CPU inference must work without a dedicated GPU.

## Optional Future Extension: Additional NER

Initially implement the anonymizer using Qwen3, regular expressions and a local case dictionary.

If evaluation shows that Qwen3 misses too many names or organizations, introduce a second independent detector using GLiNER multilingual.

Model:

https://huggingface.co/urchade/gliner_multi-v2.1

Repository:

https://github.com/urchade/GLiNER

For C# integration, investigate exporting the model to ONNX and running it using Microsoft.ML.OnnxRuntime.

https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime

Do not require Python in the final deployed application. Python may be used during development to export the model.

This extension is optional. First establish a working Qwen3-based baseline.

## Testing

Create synthetic German and Swiss German legal and administrative case descriptions.

Include:

- Unusual foreign names
- Full names and surnames
- Multiple people sharing a surname
- Company abbreviations
- Names with spelling mistakes
- Swiss addresses
- Foundations and housing cooperatives
- Debt collection references
- Court references
- Dates and financial amounts
- Multi-document cases
- Long documents
- Ambiguous words
- Unicode characters

Test at least:

1. Detection recall for each sensitive information category.
2. False negatives, particularly missed people and organizations.
3. Consistency of placeholders across multiple documents.
4. Exact round-trip restoration.
5. Handling of overlapping text spans.
6. Handling of invalid LLM JSON.
7. Handling of hallucinated entities.
8. Handling of unknown placeholders.
9. Memory consumption on 16 GB RAM.
10. Inference speed on CPU.
11. Windows self-contained publishing.
12. macOS self-contained publishing.
13. Network isolation.
14. Absence of sensitive information in logs and temporary files.

Prioritize detection recall over precision. It is preferable to flag an extra word for manual review than to expose a private name.

An LLM cannot guarantee complete anonymization. Human review must always remain available and should be required before export.

## Implementation Order

Phase 1:

Create a minimal C# console application using LLamaSharp.

Download Qwen3 8B Q4_K_M and run it locally.

Confirm that structured entity extraction works with German text on a 16 GB RAM computer.

Measure inference speed and peak memory usage.

Phase 2:

Implement regex detection, entity merging, consistent placeholders and deterministic restoration.

Create comprehensive unit tests.

Phase 3:

Implement persistent case dictionaries.

Implement the second detection pass.

Phase 4:

Implement the Avalonia desktop GUI.

Phase 5:

Publish and test self-contained Windows and macOS applications.

Phase 6:

Evaluate anonymization quality on synthetic cases and compare Qwen3 8B with Qwen3 4B.

Add GLiNER only if necessary.

## Deliverables

- Complete C# source code
- Avalonia desktop application
- Model download script
- README with setup instructions
- Unit and integration tests
- Synthetic test cases
- Windows and macOS publishing scripts
- Documentation of the privacy architecture
- Benchmark results for memory consumption, speed and detection accuracy

Start with Phase 1. Create the project, download the official Qwen3 8B GGUF model, implement local inference through LLamaSharp, and demonstrate structured extraction of private entities from a fictional German case description.

Make sure the implementation actually compiles and runs. Do not proceed to the GUI until the local inference and entity extraction work correctly.
