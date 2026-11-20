# Privacy architecture

This document describes how Hajde Ano keeps private case data on the local computer.

## Data flow

```mermaid
flowchart LR
    subgraph local["This computer (offline)"]
        direction LR
        orig["Original text<br/>(memory; draft<br/>in the case file)"]
        regex["Regex detector"]
        llm["Local LLM<br/>Qwen3 via llama.cpp<br/>(in-process)"]
        dict[("Case dictionary<br/>session folder")]
        merge["Merge spans"]
        ph["Assign placeholders"]
        anon["Anonymized text"]
        second["Second LLM pass"]
        review{{"Human review"}}
        restore["Deterministic<br/>restoration"]
        review2{{"Review"}}

        orig --> regex & llm & dict
        regex --> merge
        llm --> merge
        dict --> merge
        merge --> ph --> anon
        anon --> second -->|leftovers| merge
        ph <-->|read / extend| dict
        anon --> review
        dict --> restore --> review2
    end

    review -->|approved text only| cb1["Clipboard"]
    cb1 -.->|user pastes| ai(["Online AI service"])
    ai -.->|answer with placeholders| restore
    review2 --> cb2["Clipboard"]
```

* The **original text** is kept in memory and, as an autosaved draft of the case, in the
  case file inside the session folder (unencrypted).
* The **local LLM** runs in-process through LLamaSharp (llama.cpp). There is no server,
  no localhost port and no external process. The model only returns a JSON list of
  spans; the application checks that each span occurs in the text and performs all
  replacements itself.
* The **anonymized text** reaches the clipboard only after the user confirmed the review
  and resolved every uncertain detection.
* **Restoration** is a deterministic dictionary lookup. Unknown or modified
  placeholders are reported and left unchanged.

## What is stored, and how

| Data | Location | Protection |
|---|---|---|
| Case name, entity dictionary (original values ↔ placeholders), terms marked as not sensitive, the text of the case | `<session folder>/cases/<case id>.json` | **None** (plain JSON). The file name is the random case id and reveals nothing |
| Session marker (format version) | `<session folder>/hajde-ano-session.json` | Contains no private data |
| Settings (model path, context size, categories) and the list of recent session folders | `<data dir>/settings.json` | The folder names of the sessions are visible here |
| Anonymized documents, AI answers, restored texts | Nowhere | Kept in memory only |

The **session folder** is chosen by the user when creating or opening a session. Since
the cases are not encrypted, anyone who can read that folder can read the names and
their placeholders. Choose a folder only you can access, ideally on a disk with
full-disk encryption (BitLocker, FileVault, LUKS), and keep in mind that cloud-synced
folders (OneDrive, iCloud Drive, Dropbox) upload the cases.

The data directory is the per-user application data folder, e.g. `%APPDATA%\HajdeAno`
on Windows or `~/.config/HajdeAno` on Linux. Both paths are shown in *Settings*. The
data directory can be overridden with `HAJDE_ANO_DATA_DIR`.

### Writes and autosave

Every write goes to a temporary file in the same folder, which is flushed to the disk and
then renamed over the old file. A crash or power loss therefore leaves either the old or
the new file, never a mix. Leftover temporary files are removed when the session is
opened.

The text of the case is saved automatically 1 second after you stop typing, and at least
every 10 seconds while you keep typing, but only if it actually changed. A case file is
a few kilobytes up to some tens of kilobytes, so this is negligible for the disk: a
20 KB case saved every 10 seconds during 8 hours of continuous typing is about 60 MB a
day, while SSDs are rated for hundreds of terabytes written.

**Deletion.** Deleting a case deletes its file. Copies may remain in free disk blocks,
file system snapshots, backups or the sync history of cloud folders.

## Offline guarantees

* The application contains no networking code. `dev/continuous_integration/check_offline.py`
  (run by the pre-commit script and in CI) fails if `src/` mentions HTTP clients,
  sockets, DNS, process spawning, telemetry or logging frameworks.
* If no model is found, the application shows a local error message and asks the user
  to select a file. It never downloads one.
* llama.cpp logging is disabled through a no-op log callback, so prompts are never
  printed.
* Errors in the user interface are caught and shown with their type only. An unhandled
  exception elsewhere ends the program with a one-line message naming only the
  exception *type*; the program exits instead of aborting, so the runtime prints no
  details and no core dump is written. There is no crash reporter.
* .NET itself sends no runtime telemetry. The .NET *SDK* and Avalonia's build services
  have build-time telemetry, which only matters on developer machines; it is disabled
  in `Directory.Build.targets`, by all dev scripts and in CI (see CONTRIBUTING.md).
* The developer CLI writes the dictionary (names ↔ placeholders) as plain JSON, and only
  when asked to with `--save-dictionary`.

To verify network isolation yourself, run the application with networking disabled,
e.g. `unshare -rn ./HajdeAno` on Linux or under a firewall rule that blocks the
executable on Windows and macOS. All features keep working.

## Remaining risks

* **Detection is probabilistic.** The model can miss names, especially rare ones or
  ones in unusual contexts. Mitigations: deterministic patterns, the case dictionary
  with fuzzy matching, a second pass over the anonymized text, recall-oriented prompts,
  and mandatory human review.
* **Clipboard.** Copied text is visible to other applications and to clipboard history
  tools (e.g. Windows clipboard history), and clipboard sync can carry it to other
  devices. Only approved anonymized text and restored text are copied, at the user's
  explicit request. Restored text contains the original names.
* **Unencrypted storage.** The session folder holds the original names and the text of
  each case in plain JSON. Protect it with file permissions and full-disk encryption.
* **Save as.** *Save as…* on the input screen exports the original text to a file you
  choose, outside the session. Likewise, *Save restored Word document…* writes the
  restored answer, with the original names.
* **Swap and memory dumps.** Text in memory can be paged out by the operating system.
  Use full-disk encryption.
* **Indirect identifiers.** Even without names, a combination of details (rare
  diagnosis, small village, unusual events) can identify a person. Strict privacy mode
  replaces dates, amounts, nationalities and places, but the user remains responsible
  for the final text.
