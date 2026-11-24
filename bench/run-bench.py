#!/usr/bin/env python3
"""Interleaved unseen-script benchmark for fsion against peer runtimes.

Every round renders fresh F#/TS/JS/Python source from a per-run salt and the round
index, so no runner can serve a previously compiled artifact or cached result. All
runners execute the same round's workload interleaved, and a round's timings are
recorded only after every present runner produced byte-identical stdout.

Usage (from the repository root, after `eng/gate.ps1` has published the binaries):

    python bench/run-bench.py
    python bench/run-bench.py --rounds 30 --warmup 3 --require-ac07
    python bench/run-bench.py --require-goal0006

See bench/data.json for the workload, environment, and runner definitions.
"""

from __future__ import annotations

import argparse
import ctypes
import datetime as _datetime
import hashlib
import json
import math
import os
import platform
import re
import secrets
import shutil
import socket
import statistics
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path

BENCH_DIR = Path(__file__).resolve().parent
REPO_ROOT = BENCH_DIR.parent
DATA_FILE = BENCH_DIR / "data.json"

WORKLOAD_HELLO = "hello"
WORKLOAD_TYPICAL = "typical"
WORKLOAD_DIVERSE = "diverse"

def _windows_roaming_appdata() -> Path | None:
    if os.name != "nt":
        return None
    from ctypes import wintypes

    class _GUID(ctypes.Structure):
        _fields_ = [
            ("Data1", wintypes.DWORD),
            ("Data2", wintypes.WORD),
            ("Data3", wintypes.WORD),
            ("Data4", ctypes.c_ubyte * 8),
        ]

    folder_id = _GUID(
        0x3EB685DB,
        0x65F9,
        0x4CF6,
        (ctypes.c_ubyte * 8)(0xA0, 0x3A, 0xE3, 0xEF, 0x65, 0x72, 0x9F, 0x3D),
    )
    out = ctypes.c_wchar_p()
    hr = ctypes.windll.shell32.SHGetKnownFolderPath(ctypes.byref(folder_id), 0, None, ctypes.byref(out))
    try:
        if hr == 0 and out.value:
            return Path(out.value)
    finally:
        if out.value:
            ctypes.windll.ole32.CoTaskMemFree(out)
    fallback = os.environ.get("APPDATA", "")
    return Path(fallback) if fallback else None

def _read_data() -> dict:
    with open(DATA_FILE, "r", encoding="utf-8") as handle:
        return json.load(handle)


def _executable(name: str) -> str | None:
    return shutil.which(name)


def _fsion_binary(tfm: str) -> Path:
    override = os.environ.get("FSION_BINARY")
    if override:
        return Path(override)
    name = "fsion.exe" if os.name == "nt" else "fsion"
    return REPO_ROOT / "artifacts" / "publish" / "fsion" / tfm / name


def _fsiond_binary(tfm: str) -> Path:
    override = os.environ.get("FSIOND_PATH")
    if override:
        return Path(override)
    name = "fsiond.exe" if os.name == "nt" else "fsiond"
    return REPO_ROOT / "src" / "fsiond" / "bin" / "Release" / tfm / name


class Runner:
    def __init__(self, entry: dict, command: list[str]):
        self.id: str = entry["id"]
        self.kind: str = entry["kind"]
        self.ext: str = entry["ext"]
        self.command: list[str] = command


def _discover_runners(data: dict, tfm: str, only: list[str] | None) -> tuple[list[Runner], list[str]]:
    runners: list[Runner] = []
    missing: list[str] = []
    for entry in data["runners"]:
        if not entry.get("enabled", True):
            continue
        if only and entry["id"] not in only:
            continue
        kind = entry["kind"]
        if kind == "fsion":
            binary = _fsion_binary(tfm)
            if not binary.is_file():
                missing.append(f"{entry['id']}: {binary} (publish fsion first: pwsh -File eng/gate.ps1)")
                continue
            runners.append(Runner(entry, [str(binary)]))
        else:
            command = list(entry["command"])
            resolved = _executable(command[0])
            if resolved is None:
                missing.append(f"{entry['id']}: {command[0]} not found on PATH")
                continue
            runners.append(Runner(entry, [resolved] + command[1:]))
    return runners, missing


def _nonce(salt: str, round_index: int, workload: str) -> tuple[str, int]:
    digest = hashlib.sha256(f"{salt}:{round_index}:{workload}".encode("utf-8")).hexdigest()
    return f"r{round_index:03d}-{digest[:8]}", (int(digest[:8], 16) % 900000) + 1000


def _typical_rows(data: dict, nonce: str, seed: int) -> list[dict]:
    spec = data["typical"]
    rows = []
    for index in range(spec["rows"]):
        rows.append(
            {
                "name": f"item-{nonce}-{index}",
                "score": (seed * (index + 3)) % spec["scoreModulus"],
                "active": (index % spec["inactiveEvery"]) != 0,
            }
        )
    return rows


def _expected_output(workload: str, nonce: str, seed: int, domain: str, rows: list[dict]) -> str:
    if workload == WORKLOAD_HELLO:
        return f"hello {nonce} {seed * 2 + 7}\n"
    if workload == WORKLOAD_DIVERSE:
        return f"diverse {nonce} {seed * 2 + 7}\n"
    ordered = sorted((row for row in rows if row["active"]), key=lambda row: (-row["score"], row["name"]))
    lines = [f"domain={domain}"]
    lines.extend(f"{row['name']}|{row['score']}" for row in ordered)
    lines.append(f"count={len(ordered)}")
    return "\n".join(lines) + "\n"


def _json_literal(rows: list[dict]) -> str:
    return json.dumps(rows, separators=(",", ":"), ensure_ascii=True)


def _fsharp_source(workload: str, nonce: str, seed: int, rows: list[dict]) -> str:
    if workload == WORKLOAD_HELLO:
        return (
            "let nonce = \"" + nonce + "\"\n"
            "let seed = " + str(seed) + "\n"
            "System.Console.Out.Write(\"hello \" + nonce + \" \" + string (seed * 2 + 7) + \"\\n\")\n"
            "System.Console.Out.Flush()\n"
        )
    payload = _json_literal(rows)
    return (
        "open System\n"
        "open System.Text.Json\n"
        "\n"
        "let nonce = \"" + nonce + "\"\n"
        "let seed = " + str(seed) + "\n"
        "let raw = \"\"\"" + payload + "\"\"\"\n"
        "let domain =\n"
        "    match Environment.GetEnvironmentVariable \"BENCH_DOMAIN\" with\n"
        "    | null -> \"<missing>\"\n"
        "    | value -> value\n"
        "let ordered =\n"
        "    (JsonDocument.Parse raw).RootElement.EnumerateArray()\n"
        "    |> Seq.filter (fun element -> element.GetProperty(\"active\").GetBoolean())\n"
        "    |> Seq.map (fun element -> element.GetProperty(\"name\").GetString(), element.GetProperty(\"score\").GetInt32())\n"
        "    |> Seq.sortBy (fun (name, score) -> -score, name)\n"
        "    |> Seq.toArray\n"
        "Console.Out.Write(\"domain=\" + domain + \"\\n\")\n"
        "for name, score in ordered do\n"
        "    Console.Out.Write(name + \"|\" + string score + \"\\n\")\n"
        "Console.Out.Write(\"count=\" + string ordered.Length + \"\\n\")\n"
        "Console.Out.Flush()\n"
    )


def _js_source(workload: str, nonce: str, seed: int, rows: list[dict], typed: bool) -> str:
    declare_nonce = f"const nonce: string = \"{nonce}\";" if typed else f"const nonce = \"{nonce}\";"
    declare_seed = f"const seed: number = {seed};" if typed else f"const seed = {seed};"
    if workload == WORKLOAD_HELLO:
        return declare_nonce + "\n" + declare_seed + "\n" + "process.stdout.write(\"hello \" + nonce + \" \" + String(seed * 2 + 7) + \"\\n\");\n"
    payload = _json_literal(rows)
    typed_items = (
        "const items: Array<[string, number, boolean]> = JSON.parse(raw).map("
        "(row: { name: string; score: number; active: boolean }): [string, number, boolean] => "
        "[row.name, row.score, row.active]);"
        if typed
        else "const items = JSON.parse(raw).map((row) => [row.name, row.score, row.active]);"
    )
    header = (
        "type BenchRow = { name: string; score: number; active: boolean };\n"
        if typed
        else ""
    )
    return (
        header
        + declare_nonce + "\n"
        + declare_seed + "\n"
        + f"const raw = `{payload}`;\n"
        + "const domain = process.env.BENCH_DOMAIN ?? \"<missing>\";\n"
        + typed_items + "\n"
        + "const ordered = items\n"
        + "  .filter(([, , active]) => active)\n"
        + "  .sort((a, b) => (b[1] - a[1]) || (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0));\n"
        + "let out = \"domain=\" + domain + \"\\n\";\n"
        + "for (const [name, score] of ordered) out += name + \"|\" + score + \"\\n\";\n"
        + "out += \"count=\" + ordered.length + \"\\n\";\n"
        + "process.stdout.write(out);\n"
    )


def _python_source(workload: str, nonce: str, seed: int, rows: list[dict]) -> str:
    header = "import sys\n\n"
    if workload == WORKLOAD_HELLO:
        return (
            header
            + "nonce = " + json.dumps(nonce) + "\n"
            + "seed = " + str(seed) + "\n"
            + "sys.stdout.buffer.write((\"hello \" + nonce + \" \" + str(seed * 2 + 7) + \"\\n\").encode(\"utf-8\"))\n"
        )
    payload = _json_literal(rows)
    return (
        header
        + "import json\nimport os\n\n"
        + "nonce = " + json.dumps(nonce) + "\n"
        + "seed = " + str(seed) + "\n"
        + "raw = " + json.dumps(payload) + "\n"
        + "domain = os.environ.get(\"BENCH_DOMAIN\") or \"<missing>\"\n"
        + "items = [(row[\"name\"], row[\"score\"], row[\"active\"]) for row in json.loads(raw)]\n"
        + "ordered = sorted((row for row in items if row[2]), key=lambda row: (-row[1], row[0]))\n"
        + "out = \"domain=\" + domain + \"\\n\"\n"
        + "out += \"\".join(name + \"|\" + str(score) + \"\\n\" for name, score, _ in ordered)\n"
        + "out += \"count=\" + str(len(ordered)) + \"\\n\"\n"
        + "sys.stdout.buffer.write(out.encode(\"utf-8\"))\n"
    )


def _diverse_source(ext: str, nonce: str, seed: int) -> str:
    variant = (int(nonce.split("-")[0][1:]) - 1) % 4
    if ext == "fsx":
        setup = (
            'type Input = { Value: int }\ntype Operation = | Twice of Input | Identity of int\n'
            'let op = Twice { Value = seed }\n'
            'let value = match op with Twice { Value = n } -> n * 2 + 7 | Identity n -> n',
            'let value = [|seed; seed; 7|] |> Array.map id |> Seq.sum',
            'let value = try if seed > 0 then failwith "positive" else seed with _ -> seed * 2 + 7',
            'let value = System.Text.StringBuilder().Append(seed * 2).Append(" ").Append(7).ToString().Split()\n'
            '            |> Array.sumBy int',
        )[variant]
        return f'let seed = {seed}\n{setup}\nSystem.Console.Out.Write(sprintf "diverse {nonce} %d\\n" value)\n'
    if ext == "py":
        setup = (
            'op = ("twice", {"value": seed})\nmatch op:\n    case ("twice", {"value": n}): value = n * 2 + 7\n    case (_, n): value = n',
            'value = sum(map(lambda n: n, [seed, seed, 7]))',
            'try:\n    if seed > 0: raise ValueError("positive")\n    value = seed\nexcept ValueError:\n    value = seed * 2 + 7',
            'value = sum(map(int, " ".join([str(seed * 2), str(7)]).split()))',
        )[variant]
        return f'import sys\nseed = {seed}\n{setup}\nsys.stdout.buffer.write(f"diverse {nonce} {{value}}\\n".encode())\n'
    setup = (
        'const op = { kind: "twice", input: { value: seed } };\n'
        'const value = op.kind === "twice" ? op.input.value * 2 + 7 : op.input.value;',
        'const value = [seed, seed, 7].map(n => n).reduce((a, b) => a + b, 0);',
        'let value; try { if (seed > 0) throw new Error("positive"); value = seed; } catch { value = seed * 2 + 7; }',
        'const value = [String(seed * 2), String(7)].join(" ").split(" ").reduce((a, b) => a + Number(b), 0);',
    )[variant]
    return f'const seed = {seed};\n{setup}\nprocess.stdout.write(`diverse {nonce} ${{value}}\\n`);\n'


def _render_source(runner: Runner, workload: str, nonce: str, seed: int, rows: list[dict]) -> str:
    if workload == WORKLOAD_DIVERSE:
        return _diverse_source(runner.ext, nonce, seed)
    if runner.ext == "fsx":
        return _fsharp_source(workload, nonce, seed, rows)
    if runner.kind == "node":
        return _js_source(workload, nonce, seed, rows, typed=runner.ext == "ts")
    if runner.ext == "py":
        return _python_source(workload, nonce, seed, rows)
    raise ValueError(f"no source generator for runner {runner.id}")


def _run_command(command: list[str], work_dir: Path, env: dict, timeout: float) -> dict:
    started = time.perf_counter()
    try:
        completed = subprocess.run(
            command,
            cwd=work_dir,
            env=env,
            capture_output=True,
            timeout=timeout,
        )
        elapsed_ms = (time.perf_counter() - started) * 1000.0
        return {
            "ms": elapsed_ms,
            "exit": completed.returncode,
            "stdout": completed.stdout,
            "stderr": completed.stderr,
        }
    except subprocess.TimeoutExpired:
        elapsed_ms = (time.perf_counter() - started) * 1000.0
        return {
            "ms": elapsed_ms,
            "exit": None,
            "stdout": b"",
            "stderr": f"timed out after {timeout} s".encode("utf-8"),
        }


def _run_once(runner: Runner, script: Path, work_dir: Path, env: dict, timeout: float) -> dict:
    return _run_command(runner.command + [str(script)], work_dir, env, timeout)


def _read_metrics(endpoint: str, env: dict, timeout: float) -> dict:
    request = struct.pack("<iiB", 5, 4, 5)

    def read_response(stream) -> list[int]:
        def read_exact(size: int) -> bytes:
            data = bytearray()
            while len(data) < size:
                chunk = stream.read(size - len(data))
                if not chunk:
                    raise EOFError("daemon closed the metrics response")
                data.extend(chunk)
            return bytes(data)

        stream.write(request)
        size, = struct.unpack("<i", read_exact(4))
        fmt = "<iBiiiiqiii"
        if size != struct.calcsize(fmt):
            raise ValueError(f"unexpected metrics response size: {size}")
        version, kind, *metrics = struct.unpack(fmt, read_exact(size))
        if (version, kind) != (4, 16):
            raise ValueError(f"unexpected metrics response: {version}/{kind}")
        return metrics

    if os.name == "nt":
        with open(r"\\.\pipe" + "\\" + endpoint, "r+b", buffering=0) as stream:
            metrics = read_response(stream)
    else:
        with socket.socket(socket.AF_UNIX) as connection:
            connection.settimeout(timeout)
            directory = Path(env.get("XDG_RUNTIME_DIR") or tempfile.gettempdir())
            path = directory / (endpoint + ".sock")
            if len(os.fsencode(path)) > 100:
                if len(os.fsencode(directory / "fsion-0123456789abcdef.sock")) > 100:
                    directory = Path("/tmp")
                name_hash = hashlib.sha256(endpoint.encode()).hexdigest()[:16]
                path = directory / f"fsion-{name_hash}.sock"
            connection.connect(str(path))
            with connection.makefile("rwb", buffering=0) as stream:
                metrics = read_response(stream)
    return dict(zip(("poolTarget", "poolWarm", "coldClaims", "executed", "managedHeapBytes",
                     "scriptAssemblies", "aliveDisposedSessions", "namedSessions"), metrics))


def _burst(runners: list[Runner], data: dict, work_dir: Path, env: dict, timeout: float, salt: str) -> dict:
    plans = []
    for index in range(1, data["burstRuns"] + 1):
        nonce, seed = _nonce(salt, index, "burst")
        rows = _typical_rows(data, nonce, seed)
        expected = _expected_output(WORKLOAD_TYPICAL, nonce, seed, data["env"]["BENCH_DOMAIN"], rows).encode()
        scripts = {
            runner.id: _write_script(work_dir, f"burst-{index:02d}.{runner.ext}",
                                     _render_source(runner, WORKLOAD_TYPICAL, nonce, seed, rows))
            for runner in runners
        }
        plans.append((nonce, expected, scripts))

    before = _read_metrics(env["FSION_PIPE"], env, timeout)
    if not before["poolTarget"] or before["poolWarm"] != before["poolTarget"]:
        raise SystemExit(f"burst requires a full warm pool, got {before}")
    bursts = {}
    for runner in runners:
        values = []
        for nonce, expected, scripts in plans:
            result = _run_once(runner, scripts[runner.id], work_dir, env, timeout)
            if result["exit"] != 0 or result["stdout"] != expected:
                raise SystemExit(f"burst {runner.id} {nonce}: exit={result['exit']}, {result['stderr']!r}")
            values.append(result["ms"])
        bursts[runner.id] = {"individualMs": values, **_stats(values)}

    time.sleep(0.75)
    after = _read_metrics(env["FSION_PIPE"], env, timeout)
    if after["executed"] - before["executed"] != len(plans):
        raise SystemExit("burst execution count mismatch")
    return {
        "runners": bursts,
        "nonces": [nonce for nonce, _, _ in plans],
        "outputSha256": [hashlib.sha256(expected).hexdigest() for _, expected, _ in plans],
        "coldClaimsDelta": after["coldClaims"] - before["coldClaims"],
        "before": before,
        "after": after,
    }


def _measure_phases(runner: Runner, data: dict, directory: Path, env: dict, timeout: float, salt: str, samples: int) -> dict:
    log = directory / "phases.log"
    env = dict(env, FSIOND_LOG=str(log), FSION_PIPE=env["FSION_PIPE"] + "-phases")
    command = runner.command
    details = []
    try:
        warmup = _run_command(command + ["warmup"], directory, env, timeout)
        if warmup["exit"] != 0 or b"warm=True" not in warmup["stdout"]:
            raise SystemExit(f"phase warmup failed: {warmup['stderr']!r}")
        for index in range(1, samples + 1):
            nonce, seed = _nonce(salt, index, "phases")
            rows = _typical_rows(data, nonce, seed)
            script = _write_script(directory, f"phases-{index:02}.fsx", _fsharp_source(WORKLOAD_TYPICAL, nonce, seed, rows))
            result = _run_once(runner, script, directory, env, timeout)
            expected = _expected_output(WORKLOAD_TYPICAL, nonce, seed, data["env"]["BENCH_DOMAIN"], rows).encode()
            if result["exit"] != 0 or result["stdout"] != expected:
                raise SystemExit(f"phase parity failed: {nonce} {result['stderr']!r}")
            details.append({"nonce": nonce, "wallMs": result["ms"]})
            _read_metrics(env["FSION_PIPE"], env, timeout)
    finally:
        _run_command(command + ["daemon", "stop"], directory, env, timeout)
    trace = log.read_text(encoding="utf-8")
    evaluate = re.findall(r"run-phases evalMs=([\d.]+)", trace)
    daemon = re.findall(r"executed exit=0 .* daemonMs=([\d.]+)", trace)
    if len(evaluate) != samples or len(daemon) != samples:
        raise SystemExit(f"expected {samples} phase observations; got {len(evaluate)} eval and {len(daemon)} daemon")
    for row, eval_ms, daemon_ms in zip(details, map(float, evaluate), map(float, daemon)):
        row.update(fcsMs=eval_ms, daemonGateReleaseMs=daemon_ms - eval_ms, clientIpcMs=row["wallMs"] - daemon_ms)
    medians = {key: statistics.median(row[key] for row in details)
               for key in ("wallMs", "fcsMs", "daemonGateReleaseMs", "clientIpcMs")}
    return {"samples": details, "medians": medians}


def _percentile(sorted_values: list[float], fraction: float) -> float:
    if not sorted_values:
        return 0.0
    index = max(0, min(len(sorted_values) - 1, math.ceil(len(sorted_values) * fraction) - 1))
    return sorted_values[index]


def _stats(values: list[float]) -> dict:
    ordered = sorted(values)
    return {
        "n": len(ordered),
        "median": statistics.median(ordered),
        "mean": statistics.fmean(ordered),
        "stdev": statistics.stdev(ordered) if len(ordered) > 1 else 0.0,
        "min": ordered[0],
        "max": ordered[-1],
        "p95": _percentile(ordered, 0.95),
    }


def _fmt_ms(value: float) -> str:
    return f"{value:.1f}"


def _host_environment(tfm: str, fsion: Path, fsiond: Path) -> dict:
    return {
        "host": platform.node(),
        "platform": platform.platform(),
        "processor": os.environ.get("PROCESSOR_IDENTIFIER", platform.processor()),
        "python": sys.version.split()[0],
        "dotnet": _command_version(["dotnet", "--version"]),
        "bun": _command_version(["bun", "--version"]),
        "node": _command_version(["node", "--version"]),
        "fsionTfm": tfm,
        "fsionBinary": str(fsion),
        "fsiondBinary": str(fsiond),
        "fsionSizeBytes": fsion.stat().st_size if fsion.is_file() else None,
    }


def _command_version(command: list[str]) -> str | None:
    binary = _executable(command[0])
    if binary is None:
        return None
    try:
        completed = subprocess.run(command, capture_output=True, timeout=30, text=True)
        return completed.stdout.strip() or completed.stderr.strip()
    except Exception:
        return None


def _write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def _report_markdown(results: dict) -> str:
    lines: list[str] = []
    env = results["environment"]
    lines.append("# Goal 0006 — Unseen-Script Benchmark Report")
    lines.append("")
    lines.append(
        "Generated by `python bench/run-bench.py` on the Windows development host. "
        "Every round renders fresh F#/TS/JS/Python source from a per-run salt and the round index; "
        "timings are recorded only for rounds whose stdout was byte-identical across every present runner."
    )
    lines.append("")
    lines.append("## Environment")
    lines.append("")
    lines.append("| Item | Value |")
    lines.append("|---|---|")
    lines.append(f"| Host | `{env['host']}` |")
    lines.append(f"| Platform | `{env['platform']}` |")
    lines.append(f"| Processor | `{env['processor']}` |")
    lines.append(f"| Python | {env['python']} |")
    lines.append(f"| .NET SDK | {env['dotnet']} |")
    lines.append(f"| bun | {env['bun']} |")
    lines.append(f"| node | {env['node']} |")
    lines.append(f"| fsion TFM | `{env['fsionTfm']}` |")
    lines.append(f"| fsion binary | `{env['fsionBinary']}` ({env['fsionSizeBytes']} bytes) |")
    lines.append(f"| fsiond binary | `{env['fsiondBinary']}` |")
    lines.append(f"| Harness salt | `{results['salt']}` |")
    lines.append(f"| Measured rounds | {results['rounds']} (+{results['warmupRounds']} warmup, discarded) |")
    lines.append(f"| Workloads | {', '.join(results['workloads'])} |")
    lines.append(f"| Skipped runners | {', '.join(results['skipped']) or 'none'} |")
    lines.append(f"| Started (local) | {results['startedAt']} |")
    lines.append("")
    lines.append("## Verification")
    lines.append("")
    lines.append(
        f"All {results['rounds']} measured rounds passed byte-for-byte stdout parity across "
        f"{len(results['presentRunners'])} runners ({', '.join(results['presentRunners'])}) with exit code 0; "
        "a round's timings are only recorded after its outputs match."
    )
    lines.append("")
    lines.append("| Workload | Round | Nonce | Source SHA-256 (F#) | Output SHA-256 | Verified |")
    lines.append("|---|---|---|---|---|---|")
    for round_result in results["roundsDetail"]:
        for workload, detail in round_result["workloads"].items():
            lines.append(
                f"| {workload} | {round_result['round']} | `{detail['nonce']}` | "
                f"`{detail['fsharpSourceSha256'][:16]}…` | `{detail['outputSha256'][:16]}…` | "
                f"{'yes' if detail['verified'] else 'NO'} |"
            )
    lines.append("")
    lines.append("## Latency (milliseconds, per process invocation)")
    lines.append("")
    for workload in results["workloads"]:
        lines.append(f"### {workload}")
        lines.append("")
        aggregates = results["aggregates"][workload]
        lines.append("| Runner | Median | Mean | Stdev | Min | p95 | Max | n |")
        lines.append("|---|---|---|---|---|---|---|---|")
        for runner_id in results["presentRunners"]:
            stats = aggregates.get(runner_id)
            if stats is None:
                continue
            lines.append(
                f"| {runner_id} | {_fmt_ms(stats['median'])} | {_fmt_ms(stats['mean'])} | "
                f"{_fmt_ms(stats['stdev'])} | {_fmt_ms(stats['min'])} | {_fmt_ms(stats['p95'])} | "
                f"{_fmt_ms(stats['max'])} | {stats['n']} |"
            )
        lines.append("")
        fsion_stats = aggregates.get("fsion")
        if fsion_stats:
            lines.append("| Comparison | Median ratio | Threshold | Verdict |")
            lines.append("|---|---|---|---|")
            fsi_stats = aggregates.get(results.get("fsiRunnerId") or "")
            bun_stats = aggregates.get("bun")
            if fsi_stats:
                speedup = fsi_stats["median"] / fsion_stats["median"]
                verdict = "PASS" if speedup >= results["ac07"]["minSpeedupOverFsi"] else "FAIL"
                lines.append(
                    f"| fsion vs dotnet fsi | {speedup:.2f}x faster | "
                    f">= {results['ac07']['minSpeedupOverFsi']:.0f}x | {verdict} |"
                )
            if bun_stats:
                ratio = fsion_stats["median"] / bun_stats["median"]
                verdict = "PASS" if ratio <= results["ac07"]["maxRatioToBun"] else "FAIL"
                lines.append(
                    f"| fsion vs bun | {ratio:.3f}x bun's median | "
                    f"<= {results['ac07']['maxRatioToBun']:.2f}x | {verdict} |"
                )
            lines.append("")
        if workload == WORKLOAD_TYPICAL:
            per_round = ", ".join(_fmt_ms(value) for value in results["fsionPerRound"][workload])
            lines.append(f"fsion per-round medians ({workload}, ms): {per_round}")
            median = aggregates["fsion"]["median"]
            outliers = [value for value in results["fsionPerRound"][workload] if value > median * 5.0]
            lines.append("")
            lines.append(
                f"Rounds above 5x the fsion median (possible cold claims): {len(outliers)}"
                + (f" — {', '.join(_fmt_ms(value) for value in outliers)}" if outliers else "")
            )
            lines.append("")
    if results["burst"]:
        burst = results["burst"]
        lines.extend(["## Zero-gap burst", "", "| Runner | Individual ms | Median | p95 | Max |",
                      "|---|---|---|---|---|"])
        for runner, stats in burst["runners"].items():
            samples = ", ".join(map(_fmt_ms, stats["individualMs"]))
            lines.append(f"| {runner} | {samples} | {stats['median']:.1f} | {stats['p95']:.1f} | {stats['max']:.1f} |")
        lines.extend(["", f"ColdClaims delta: {burst['coldClaimsDelta']}; "
                      f"post-idle AliveDisposedSessions: {burst['after']['aliveDisposedSessions']}.", ""])
    if results["phases"]:
        phases = results["phases"]
        lines.extend(["## Typical phase probe", "",
                      "Separate spaced runs with daemon logging enabled; not part of the benchmark aggregates.",
                      "FCS includes evaluation bookkeeping. Daemon gate/release includes worker scheduling and claim; "
                      "client + IPC is the residual from process wall time minus the daemon's execution interval.", "",
                      "| Wall | Client + IPC | Daemon gate/release | FCS compile + exec | n |",
                      "|---|---|---|---|---|"])
        values = " | ".join(_fmt_ms(phases["medians"][key])
                            for key in ("wallMs", "clientIpcMs", "daemonGateReleaseMs", "fcsMs"))
        lines.extend([f"| {values} | {len(phases['samples'])} |", ""])
    if results["goal0006"]:
        lines.extend(["## Goal 0006 thresholds", ""])
        for label, (actual, limit, passed) in results["goal0006"].items():
            lines.append(f"- {label}: {actual:.3f} ({limit}) — {'PASS' if passed else 'FAIL'}")
        lines.append("")
    lines.append("## AC-07 Verdict")
    lines.append("")
    verdicts = results["ac07"]["verdicts"]
    if not verdicts:
        lines.append("No comparison could be evaluated (fsion or dotnet fsi missing).")
    for verdict in verdicts:
        lines.append(
            f"- **{verdict['label']}**: {verdict['detail']} — {'PASS' if verdict['pass'] else 'FAIL'}"
        )
    lines.append("")
    overall = all(verdict["pass"] for verdict in verdicts) if verdicts else False
    lines.append(f"Overall AC-07 (on this host, this run): **{'PASS' if overall else 'FAIL'}**")
    lines.append("")
    lines.append("## Raw Results")
    lines.append("")
    lines.append(f"Raw per-round timings and hashes: `{results['resultsPath']}`.")
    lines.append("")
    return "\n".join(lines)


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rounds", type=int, default=None, help="measured rounds (default from bench/data.json)")
    parser.add_argument("--warmup", type=int, default=None, help="warmup rounds whose timings are discarded")
    parser.add_argument("--tfm", default=None, help="fsion/fsiond target framework (default net10.0)")
    parser.add_argument("--workloads", default="hello,typical,diverse", help="comma-separated workloads")
    parser.add_argument("--runners", default=None, help="comma-separated runner ids (default: all present)")
    parser.add_argument("--report", default=str(REPO_ROOT / "docs" / "evidence" / "0006-benchmark-report.md"))
    parser.add_argument(
        "--results", default=str(REPO_ROOT / "docs" / "evidence" / "0006-benchmark-results.json")
    )
    parser.add_argument("--timeout", type=float, default=120.0, help="per-run timeout in seconds")
    parser.add_argument("--phase-samples", type=int, default=0, help="separate spaced typical runs with daemon phase logging")
    parser.add_argument("--require-ac07", action="store_true", help="exit 1 when an AC-07 threshold fails")
    parser.add_argument("--require-goal0006", action="store_true", help="exit 1 when a Goal 0006 threshold fails")
    parser.add_argument("--keep", action="store_true", help="keep the generated script directory")
    parser.add_argument(
        "--work-dir",
        default=None,
        help="parent directory for the harness-owned run-<salt> script directory (default bench/.work/ "
        "inside the repository; all runners share the generated scripts so no runtime pays a "
        "path-specific penalty)",
    )
    parser.add_argument("--dry-run", action="store_true", help="render round 1 sources and exit")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = _parse_args(argv)
    data = _read_data()
    tfm = args.tfm or data["defaults"]["tfm"]
    rounds = args.rounds if args.rounds is not None else data["defaults"]["rounds"]
    warmup_rounds = args.warmup if args.warmup is not None else data["defaults"]["warmupRounds"]
    workloads = [item.strip() for item in args.workloads.split(",") if item.strip()]
    only = [item.strip() for item in args.runners.split(",")] if args.runners else None

    for workload in workloads:
        if workload not in data["workloads"]:
            raise SystemExit(f"unknown workload: {workload} (bench/data.json defines {data['workloads']})")
    if rounds < 1 or warmup_rounds < 0 or args.phase_samples < 0:
        raise SystemExit("--rounds must be >= 1, --warmup and --phase-samples must be >= 0")

    fsion_path = _fsion_binary(tfm)
    fsiond_path = _fsiond_binary(tfm)
    runners, skipped = _discover_runners(data, tfm, only)
    if not runners:
        raise SystemExit("no benchmark runners are available; " + "; ".join(skipped))
    if not any(runner.id == "fsion" for runner in runners):
        raise SystemExit("the fsion runner is required for this benchmark")

    present_ids = {runner.id for runner in runners}
    for enabled, flag, required_workloads, min_rounds in (
        (args.require_ac07, "--require-ac07", (WORKLOAD_TYPICAL,), data["defaults"]["rounds"]),
        (args.require_goal0006, "--require-goal0006", (WORKLOAD_HELLO, WORKLOAD_TYPICAL, WORKLOAD_DIVERSE), 30),
    ):
        if not enabled:
            continue
        if args.dry_run:
            raise SystemExit(f"--dry-run cannot satisfy {flag} (no timings or comparisons were measured)")
        missing = [f"workload '{name}' is required" for name in required_workloads if name not in workloads]
        if rounds < min_rounds:
            missing.append(f"--rounds must be >= {min_rounds} (got {rounds})")
        required_runners = (
            ("fsion", "dotnet-fsi", "bun", "node", "python")
            if flag == "--require-goal0006"
            else ("fsion", "dotnet-fsi", "bun")
        )
        for required_runner in required_runners:
            if required_runner not in present_ids:
                missing.append(f"runner '{required_runner}' is missing")
        if flag == "--require-goal0006" and args.phase_samples < 1:
            missing.append("--phase-samples must be >= 1 when --require-goal0006 is set")
        if missing:
            raise SystemExit(f"{flag} preconditions not met: " + "; ".join(missing))

    if args.dry_run:
        salt = secrets.token_hex(4)
        for runner in runners:
            nonce, seed = _nonce(salt, 1, WORKLOAD_TYPICAL)
            rows = _typical_rows(data, nonce, seed)
            print(f"===== {runner.id} (round 1, {WORKLOAD_TYPICAL}) =====")
            print(_render_source(runner, WORKLOAD_TYPICAL, nonce, seed, rows))
        return 0

    parent_dir = Path(args.work_dir).resolve() if args.work_dir else (BENCH_DIR / ".work")
    parent_dir.mkdir(parents=True, exist_ok=True)
    salt = ""
    work_dir = parent_dir
    for _ in range(16):
        candidate_salt = secrets.token_hex(4)
        candidate_dir = parent_dir / f"run-{candidate_salt}"
        try:
            candidate_dir.mkdir(parents=False, exist_ok=False)
            salt = candidate_salt
            work_dir = candidate_dir
            break
        except FileExistsError:
            continue
    if not salt:
        raise SystemExit(f"could not allocate a fresh run-<salt> directory under {parent_dir}")

    if os.name == "nt":
        roaming_dir = _windows_roaming_appdata()
        default_prelude = roaming_dir / "fsion" / "prelude.fsx" if roaming_dir else None
        if default_prelude and default_prelude.exists():
            raise SystemExit(
                f"default user prelude {default_prelude} exists; move it aside before running the default-path benchmark"
            )
    base_env = dict(os.environ)
    base_env.pop("FSION_PRELUDE", None)
    base_env.pop("FSION_POOL_SIZE", None)
    base_env.pop("FSIOND_LOG", None)
    base_env.update(data["env"])
    base_env["FSION_PIPE"] = f"fsion-bench-{salt}"
    base_env["FSIOND_PATH"] = str(fsiond_path)
    base_env.pop("FSION_CONFIG_DIR", None)
    if os.name != "nt":
        config_dir = work_dir / ".fsion-config"
        config_dir.mkdir(parents=True, exist_ok=True)
        base_env["XDG_CONFIG_HOME"] = str(config_dir)
    present = [runner.id for runner in runners]
    print(f"benchmark salt {salt}; runners: {', '.join(present)}")
    if skipped:
        print("skipped: " + "; ".join(skipped))

    started_at = _datetime.datetime.now().astimezone().isoformat(timespec="seconds")
    fsion_runner = next(runner for runner in runners if runner.id == "fsion")

    rounds_detail: list[dict] = []
    totals: dict[str, dict[str, list[float]]] = {
        workload: {runner_id: [] for runner_id in present} for workload in workloads
    }
    fsion_per_round: dict[str, list[float]] = {workload: [] for workload in workloads}
    measured = 0
    burst = None
    phases = None

    try:
        warmup = _run_command([fsion_runner.command[0], "--pool-size", str(data["burstRuns"]), "warmup"], work_dir, base_env, args.timeout)
        warmup_out = warmup["stdout"].decode("utf-8", "replace").strip()
        if warmup["exit"] != 0 or "warm=True" not in warmup_out:
            _write_text(work_dir / "warmup.err", warmup["stderr"].decode("utf-8", "replace") + "\n" + warmup_out)
            raise SystemExit(f"fsion warmup did not fill a warm pool (exit={warmup['exit']}, out={warmup_out!r})")
        print("fsion warmup: " + warmup_out)
        for round_index in range(0, warmup_rounds + rounds):
            measured_round = round_index >= warmup_rounds
            round_number = round_index - warmup_rounds + 1
            order = runners[round_index % len(runners) :] + runners[: round_index % len(runners)]
            round_entry = {"round": round_number, "workloads": {}} if measured_round else None
            for workload in workloads:
                nonce, seed = _nonce(salt, round_index + 1, workload)
                rows = _typical_rows(data, nonce, seed)
                expected = _expected_output(workload, nonce, seed, data["env"]["BENCH_DOMAIN"], rows)
                outputs: dict[str, bytes] = {}
                timings: dict[str, float] = {}
                for runner in order:
                    source = _render_source(runner, workload, nonce, seed, rows)
                    script = _write_script(work_dir, f"round{round_index + 1:03d}-{workload}-{runner.id}.{runner.ext}", source)
                    if measured_round and runner.id == "fsion":
                        fsion_source_hash = hashlib.sha256(source.encode("utf-8")).hexdigest()
                    result = _run_once(runner, script, work_dir, base_env, args.timeout)
                    if result["exit"] != 0:
                        raise SystemExit(
                            f"round {round_number} {workload} {runner.id} exited with {result['exit']}: "
                            f"{result['stderr'].decode('utf-8', 'replace')[:2000]}"
                        )
                    outputs[runner.id] = result["stdout"]
                    timings[runner.id] = result["ms"]
                expected_bytes = expected.encode("utf-8")
                mismatched = [runner_id for runner_id, output in outputs.items() if output != expected_bytes]
                if mismatched:
                    for runner_id in mismatched:
                        print(f"----- {runner_id} stdout (round {round_number} {workload}) -----")
                        print(outputs[runner_id].decode("utf-8", "replace"))
                    raise SystemExit(
                        f"round {round_number} {workload}: stdout mismatch in {mismatched}; "
                        f"expected:\n{expected}"
                    )
                if measured_round:
                    for runner_id, value in timings.items():
                        totals[workload][runner_id].append(value)
                    fsion_per_round[workload].append(timings["fsion"])
                    round_entry["workloads"][workload] = {
                        "nonce": nonce,
                        "fsharpSourceSha256": fsion_source_hash,
                        "outputSha256": hashlib.sha256(expected_bytes).hexdigest(),
                        "verified": True,
                        "timingsMs": timings,
                    }
            if measured_round:
                measured += 1
                rounds_detail.append(round_entry)
                fsion_ms = round_entry["workloads"].get(WORKLOAD_TYPICAL, {}).get("timingsMs", {}).get("fsion")
                if fsion_ms is not None:
                    print(
                        f"round {round_number}/{rounds}"
                        f"  fsion={fsion_ms:.1f}ms"
                        + "".join(
                            f"  {runner}={round_entry['workloads'][WORKLOAD_TYPICAL]['timingsMs'][runner]:.1f}ms"
                            for runner in present
                            if runner != "fsion"
                        )
                    )
        burst_runners = [runner for runner in runners if runner.id in ("fsion", "bun")]
        if WORKLOAD_TYPICAL in workloads and len(burst_runners) == 2:
            burst = _burst(burst_runners, data, work_dir, base_env, args.timeout, salt)
        if args.phase_samples:
            shutdown = _run_command(fsion_runner.command + ["daemon", "stop"], work_dir, base_env, args.timeout)
            if shutdown["exit"] != 0:
                raise SystemExit(f"benchmark daemon did not stop: {shutdown['stderr']!r}")
            phases = _measure_phases(fsion_runner, data, work_dir, base_env, args.timeout, salt, args.phase_samples)
    finally:
        try:
            subprocess.run(
                [str(fsion_path), "daemon", "stop"],
                cwd=work_dir,
                env=base_env,
                capture_output=True,
                timeout=60,
            )
        except Exception:
            pass
        if not args.keep:
            shutil.rmtree(work_dir, ignore_errors=True)
        else:
            print(f"generated scripts kept in {work_dir}")
    aggregates = {
        workload: {
            runner_id: _stats(values) for runner_id, values in totals[workload].items() if values
        }
        for workload in workloads
    }

    fsi_runner_id = "dotnet-fsi" if "dotnet-fsi" in present else None
    verdicts: list[dict] = []
    typical = aggregates.get(WORKLOAD_TYPICAL, {})
    fsion_stats = typical.get("fsion")
    if fsion_stats and fsi_runner_id and typical.get(fsi_runner_id):
        speedup = typical[fsi_runner_id]["median"] / fsion_stats["median"]
        passed = speedup >= data["ac07"]["minSpeedupOverFsi"]
        verdicts.append(
            {
                "label": "fsion >= 20x median speedup over dotnet fsi (typical)",
                "detail": f"{speedup:.2f}x (fsion {_fmt_ms(fsion_stats['median'])} ms vs fsi {_fmt_ms(typical[fsi_runner_id]['median'])} ms)",
                "pass": passed,
            }
        )
    if fsion_stats and typical.get("bun"):
        ratio = fsion_stats["median"] / typical["bun"]["median"]
        passed = ratio <= data["ac07"]["maxRatioToBun"]
        verdicts.append(
            {
                "label": "fsion reaches bun-class latency (typical)",
                "detail": f"fsion is {ratio:.3f}x bun's median (fsion {_fmt_ms(fsion_stats['median'])} ms vs bun {_fmt_ms(typical['bun']['median'])} ms)",
                "pass": passed,
            }
        )

    thresholds = {}
    baselines = {WORKLOAD_TYPICAL: 84.9, WORKLOAD_DIVERSE: 75.6}
    for name in (WORKLOAD_HELLO, WORKLOAD_TYPICAL, WORKLOAD_DIVERSE):
        stats = aggregates.get(name, {})
        if "fsion" not in stats:
            continue
        median = stats["fsion"]["median"]
        if "dotnet-fsi" in stats:
            speedup = stats["dotnet-fsi"]["median"] / median
            limit = 50 if name == WORKLOAD_HELLO else 40
            thresholds[f"{name} FSI speedup"] = (speedup, f">= {limit}x", speedup >= limit)
        if name == WORKLOAD_HELLO and "bun" in stats:
            bun = stats["bun"]["median"]
            thresholds["hello fsion minus bun ms"] = (median - bun, "<= 3 ms or <= 1.05x bun", median <= max(bun + 3, 1.05 * bun))
        if name in baselines:
            baseline = baselines[name]
            thresholds[f"{name} fsion median ms"] = (median, f"< {baseline} ms", median < baseline)
    if burst:
        ratio = burst["runners"]["fsion"]["median"] / typical["fsion"]["median"]
        maximum = burst["runners"]["fsion"]["max"]
        alive = burst["after"]["aliveDisposedSessions"]
        thresholds["burst median vs spaced typical"] = (ratio, "<= 1.25x", ratio <= 1.25)
        thresholds["burst max ms"] = (maximum, "< 750 ms", maximum < 750)
        thresholds["burst alive disposed"] = (alive, "zero", alive == 0)

    results = {
        "schema": 2,
        "burst": burst,
        "phases": phases,
        "goal0006": thresholds,
        "startedAt": started_at,
        "finishedAt": _datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
        "salt": salt,
        "rounds": measured,
        "warmupRounds": warmup_rounds,
        "workloads": workloads,
        "presentRunners": present,
        "skipped": skipped,
        "fsiRunnerId": fsi_runner_id,
        "environment": _host_environment(tfm, fsion_path, fsiond_path),
        "aggregates": aggregates,
        "fsionPerRound": fsion_per_round,
        "roundsDetail": rounds_detail,
        "ac07": {
            "minSpeedupOverFsi": data["ac07"]["minSpeedupOverFsi"],
            "maxRatioToBun": data["ac07"]["maxRatioToBun"],
            "verdicts": verdicts,
        },
        "resultsPath": args.results,
    }

    report = _report_markdown(results)
    _write_text(Path(args.report), report)
    _write_text(Path(args.results), json.dumps(results, indent=2) + "\n")
    print(report)

    failed = [verdict for verdict in verdicts if not verdict["pass"]]
    if args.require_ac07 and (failed or not verdicts):
        return 1
    if args.require_goal0006 and (not thresholds or not all(passed for _, _, passed in thresholds.values())):
        return 1
    return 0


def _write_script(work_dir: Path, name: str, source: str) -> Path:
    path = work_dir / name
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(source)
    return path


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    raise SystemExit(main(sys.argv[1:]))
