# Benchmarks

Measurements of detection quality, speed and memory. All numbers come from the
**synthetic** cases in [`tests/data/cases/`](../tests/data/cases/) (6 cases, 9 documents,
3 593 characters, 104 annotated entity occurrences).

Reproduce them with:

```sh
dotnet run -c Release --project src/HajdeAno.Cli -- evaluate --report report.md
dotnet run -c Release --project src/HajdeAno.Cli -- evaluate --model models/Qwen3-8B-Q4_K_M.gguf --report report.md
```

*Recall* counts an annotated entity as covered in a document only if **every**
occurrence of it lies inside a replaced span. Precision is not measured: by design,
extra detections are flagged for review rather than counted as errors.

## Test machine

* Intel Core i7-6600U (2 cores / 4 threads, 2016 laptop CPU, AVX2), 16 GB RAM, no GPU
* Linux, .NET 10.0.4, LLamaSharp 0.30.0 (llama.cpp CPU backend)
* Context size 4096, CPU only (`GpuLayerCount = 0`)

## Results

### Patterns only (no model)

Measured 2026-11-15. Runs in 0.2 s with a 50 MiB peak working set.

**Overall recall: 43.3 %** (45/104)

| Category | Recall | Covered / Total |
|---|---:|---:|
| ADDRESS | 100.0 % | 4 / 4 |
| AMOUNT | 100.0 % | 6 / 6 |
| CASE_NUMBER | 100.0 % | 2 / 2 |
| CITY | 20.0 % | 1 / 5 |
| COUNTRY | 0.0 % | 0 / 1 |
| COURT | 0.0 % | 0 / 1 |
| CUSTOMER_NUMBER | 66.7 % | 2 / 3 |
| DATE | 100.0 % | 10 / 10 |
| DATE_OF_BIRTH | 100.0 % | 2 / 2 |
| DEBT_COLLECTION_REF | 100.0 % | 1 / 1 |
| EMAIL | 100.0 % | 3 / 3 |
| FAMILY | 0.0 % | 0 / 2 |
| GOVERNMENT_AGENCY | 0.0 % | 0 / 2 |
| IBAN | 100.0 % | 2 / 2 |
| NATIONALITY | 0.0 % | 0 / 3 |
| ORGANIZATION | 0.0 % | 0 / 13 |
| ORG_ABBREVIATION | 0.0 % | 0 / 2 |
| PERMIT_NUMBER | 100.0 % | 2 / 2 |
| PERSON | 0.0 % | 0 / 30 |
| PHONE | 100.0 % | 3 / 3 |
| POSTAL_CODE | 100.0 % | 5 / 5 |
| SOCIAL_SECURITY_NUMBER | 100.0 % | 1 / 1 |
| URL | 100.0 % | 1 / 1 |

The patterns reliably catch the structured identifiers. Names, organizations,
authorities, courts and nationalities need the language model, which is why the
application warns when no model is loaded.

### Qwen3-0.6B-Q8_0 (development model)

Measured 2026-11-15 on the first document of `01-raeumung` (502 characters), first pass
only:

| Metric | Value |
|---|---:|
| Model load | 0.8 s |
| Inference (one chunk) | 17.6 s |
| Peak working set | 1 187 MiB |
| Entities reported / hallucinated | 8 / 0 |
| Invalid JSON | 0 |

It found the people, the address, the city, the amount, the phone number and the e-mail,
but missed both organizations, the dates, the debt-collection reference and the date of
birth. The patterns cover the latter, but the organizations stay undetected. Use it for
development only.

### Qwen3-8B-Q4_K_M (default model)

| Metric | Value |
|---|---:|
| Model load (memory-mapped, warm disk cache) | 14.3 s |
| Resident memory during inference | ≈ 5.2 GB |
| Recall per category | **pending** |
| Inference speed | **pending** |

The full evaluation (two passes over 9 documents) takes longer than an hour on this 2016
dual-core CPU. The first run was disturbed by concurrent builds and stopped before it
finished, so no recall or speed numbers are reported yet. Rerun the command above on an
otherwise idle machine and fill in this section.

### Qwen3-4B-Q4_K_M

Not measured yet.

## Memory on 16 GB RAM

The 8B model ran next to a desktop session, an IDE and builds on the 16 GB test machine.
Its resident memory stayed at about 5.2 GB, in line with the design estimate
(about 5 GB of weights plus 1–2 GB of context and buffers). Only one model is loaded at a
time, and switching models in *Settings* unloads the previous one first.

## Notes

* All runs are CPU-only. Apple Silicon with Metal (*Use GPU* in *Settings*) and newer
  CPUs with more cores are expected to be considerably faster.
* The second pass roughly doubles the inference time. Disable it in *Settings* for
  faster but less safe detection.
