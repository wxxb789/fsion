# Goal 0009 - Windows PowerShell 5.1 & PowerShell 7 Unseen-Script Benchmark Comparison

## Purpose

Extend the reproducible unseen-script benchmark harness (`bench/run-bench.py` and `bench/data.json`) with **Windows PowerShell 5.1** (`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`) and **PowerShell 7+** (`pwsh -NoProfile -NonInteractive -File`), update the protocol metrics reader to `ProtocolVersion = 6`, and record an end-to-end 30-round interleaved unseen-script + 12-run zero-gap burst benchmark on the post-Goal-0008 `fsion` build.

## Outcomes & Verification Contract

1. **Runner integration (`bench/data.json`, `bench/run-bench.py`)**:
   - Add `pwsh` (`pwsh -NoProfile -NonInteractive -File`) and `powershell` (`powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`) runners with `.ps1` unseen-script generators for `hello`, `typical`, and `diverse` (4 rotating variants).
   - Output from `.ps1` scripts uses explicit UTF-8 LF (`[Console]::Out.Write(...)`) so every round achieves byte-identical stdout parity with `fsion`, `dotnet-fsi`, `bun`, `node`, and `python`.
   - On non-Windows hosts where `powershell.exe` is absent, `_discover_runners` cleanly skips it without failing the harness.
   - `_read_metrics` uses current `ProtocolVersion = 6` (matching `Fsion.Protocol`).
2. **Post-Goal-0008 benchmark evidence (`docs/evidence/0009-benchmark-report.md`, `docs/evidence/0009-benchmark-results.json`, `docs/evidence/0009-evidence.md`)**:
   - Run `python bench/run-bench.py --rounds 30 --warmup 3 --require-goal0006 --report docs/evidence/0009-benchmark-report.md --results docs/evidence/0009-benchmark-results.json` against the packaged `net10.0` `fsion` binary on Windows.
   - Verify all 7 runners (`fsion`, `dotnet-fsi`, `bun`, `node`, `python`, `pwsh`, `powershell`) produce byte-identical stdout across all 30 rounds x 3 workloads + 12-run zero-gap burst.
   - Verify Goal 0006 thresholds (`--require-goal0006`) remain green (zero regression after Goals 0007 and 0008).
3. **Documentation (`README.md`, `docs/goals/README.md`)**:
   - Update `README.md` benchmark table with the latest post-0008 numbers including `pwsh` and `powershell`.
   - Archive Goal 0009 upon completion.
