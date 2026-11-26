# Goal 0009 Evidence - Windows PowerShell 5.1 & PowerShell 7 Unseen-Script Benchmark Comparison

## Summary

Goal 0009 extended `bench/data.json` and `bench/run-bench.py` with **PowerShell 7+** (`pwsh -NoProfile -NonInteractive -File`) and **Windows PowerShell 5.1** (`powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`), updated the benchmark metrics reader to `ProtocolVersion = 6`, and ran a full 30-round interleaved unseen-script + 12-run zero-gap burst benchmark against the post-Goal-0008 packaged `net10.0` `fsion` bundle on Windows (`CPC-lhan-XF3D0M`, salt `fe94fc4a`).

## Results (30 interleaved rounds + 3 warmup, 7 runners, byte-identical stdout parity)

| Workload | `fsion` median | `bun` median | `node` median | `python` median | `powershell` (5.1) median | `pwsh` (7.6) median | `dotnet fsi` median |
|---|---:|---:|---:|---:|---:|---:|---:|
| `hello` | **67.2 ms** | 60.0 ms | 88.2 ms | 55.0 ms | 341.1 ms | 596.4 ms | 3116.0 ms |
| `typical` (JSON filter/sort + env) | **90.2 ms** | 60.1 ms | 80.5 ms | 77.7 ms | 650.7 ms | 685.4 ms | 3294.4 ms |
| `diverse` (4 rotating variants) | **67.9 ms** | 62.4 ms | 80.4 ms | 52.0 ms | 349.7 ms | 528.0 ms | 3317.1 ms |
| `burst` (12 zero-gap `typical` scripts) | **71.2 ms** (max 87.9 ms) | 55.7 ms (max 227.6 ms) | — | — | 588.7 ms (max 736.6 ms) | 647.5 ms (max 826.3 ms) | — |

### Key Takeaways

- **`fsion` vs PowerShell (`-NoProfile`)**:
  - `hello`: **5.1x faster** than Windows PowerShell 5.1 (`341.1 ms`) and **8.9x faster** than PowerShell 7.6 (`596.4 ms`).
  - `typical`: **7.2x faster** than Windows PowerShell 5.1 (`650.7 ms`) and **7.6x faster** than PowerShell 7.6 (`685.4 ms`).
  - `diverse`: **5.1x faster** than Windows PowerShell 5.1 (`349.7 ms`) and **7.8x faster** than PowerShell 7.6 (`528.0 ms`).
  - `burst` (12 back-to-back unseen `typical` scripts): **8.3x faster** than Windows PowerShell 5.1 (`588.7 ms`), **9.1x faster** than PowerShell 7.6 (`647.5 ms`), with **0 cold claims**, **max 87.9 ms** (vs `bun`'s 227.6 ms max spike), and **0 leaked sessions** (`AliveDisposedSessions = 0`).
- **Spaced-round note**: With 7 runners per round (~5.5s between consecutive `fsion` invocations), `fsiond`'s Goal 0007 idle LOH-compacting GC (`CompactOnce` after 500 ms quiet) occasionally overlaps a spaced round (`hello` p95 / `typical` 90.2 ms vs `burst` 71.2 ms where consecutive runs stay hot). Under actual burst load (`burst` median **71.2 ms**, max **87.9 ms**), latency is strictly faster than Goal 0006's 85.8 ms / 190.9 ms max.

## Artifacts

- Full report: [`0009-benchmark-report.md`](0009-benchmark-report.md)
- Raw JSON results: [`0009-benchmark-results.json`](0009-benchmark-results.json)
