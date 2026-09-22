"""Validate and summarize completed cost/FP32 experiment records, without rerunning GPU work."""
import json
import statistics
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "docs" / "arithmetic-experiment"
cost = []
experiments = []
median = statistics.median
for path in sorted(root.glob("cost-*.jsonl")):
    records = [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines()]
    assert records[-1]["phase"] == "passed", path
    for baseline in (r for r in records if r["phase"] == "baseline"):
        frames = [r for r in records if r["phase"] == "completed" and r["fixture"] == baseline["fixture"]]
        assert len(frames) == 8 and all(r["hash"] == baseline["hash"] for r in frames), path
        for frame in frames:
            for key in ("ReferencePasses", "RepairedCount", "Float64GlitchCount"):
                assert frame[key] == baseline[key], (path, key)
        measured = [r for r in frames if r["measured"] and not r["warmup"]]
        quiet = [r for r in frames if not r["measured"] and not r["warmup"]]
        assert len(measured) == len(quiet) == 3
        for frame in measured:
            t = frame["timings"]
            assert t["JournalWriteCount"] == 0 and t["OtherHostMilliseconds"] >= -0.1
            assert abs(t["DispatchMilliseconds"] - t["SubmissionMilliseconds"] - t["CompletionWaitMilliseconds"]) <= 0.1
            assert abs(t["Float64DispatchMilliseconds"] - t["Float64SubmissionMilliseconds"] - t["Float64CompletionWaitMilliseconds"]) <= 0.1
            assert abs(t["DoubleDoubleDispatchMilliseconds"] - t["DoubleDoubleSubmissionMilliseconds"] - t["DoubleDoubleCompletionWaitMilliseconds"]) <= 0.1
        fields = ["Float64DispatchMilliseconds", "DoubleDoubleDispatchMilliseconds", "SubmissionMilliseconds",
                  "CompletionWaitMilliseconds", "ReadbackMilliseconds", "RepairMilliseconds", "ReferenceMilliseconds",
                  "BlaMilliseconds", "UploadMilliseconds", "ColoringMilliseconds", "OtherHostMilliseconds",
                  "Float64SubmissionMilliseconds", "DoubleDoubleSubmissionMilliseconds",
                  "Float64CompletionWaitMilliseconds", "DoubleDoubleCompletionWaitMilliseconds", "DispatchCount"]
        row = dict(file=path.name, fixture=baseline["fixture"], width=baseline["width"], height=baseline["height"],
                   hash=baseline["hash"], quietMs=median(r["milliseconds"] for r in quiet),
                   timedMs=median(r["milliseconds"] for r in measured),
                   timings={f: median(r["timings"][f] for r in measured) for f in fields})
        cost.append(row)
        print(f"COST {row['width']:4} {row['fixture']:12} quiet={row['quietMs']:.2f} timed={row['timedMs']:.2f} "
              f"FP64={row['timings']['Float64DispatchMilliseconds']:.2f} DD={row['timings']['DoubleDoubleDispatchMilliseconds']:.2f}")

for path in sorted(root.glob("fp32-*.jsonl")):
    records = [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines()]
    assert records[-1]["phase"] == "passed", path
    for raw in (r for r in records if r["phase"] == "raw"):
        frames = [r for r in records if r["phase"] == "completed" and r["name"] == raw["name"] and not r["warmup"]]
        baseline = [r for r in frames if not r["candidate"]]
        candidate = [r for r in frames if r["candidate"]]
        assert len(baseline) == len(candidate) == 4
        assert all(r["ExperimentalAccepted"] == raw["accepted"] for r in candidate)
        for key in ("Float64GlitchCount", "RepairedCount"):
            assert len({r[key] for r in frames}) == 1, (path, raw["name"], key)
        b = median(r["milliseconds"] for r in baseline)
        c = median(r["milliseconds"] for r in candidate)
        row = dict(file=path.name, fixture=raw["name"], width=raw["width"], height=raw["height"],
                   accepted=raw["accepted"], acceptedPercent=100 * raw["accepted"] / (raw["width"] * raw["height"]),
                   fp64Accepted=raw["fp64Accepted"], samples=raw["samples"], hash=raw["hash"],
                   baselineMs=b, candidateMs=c, reductionPercent=100 * (1-c/b),
                   baselineSamples=[r["milliseconds"] for r in baseline], candidateSamples=[r["milliseconds"] for r in candidate])
        experiments.append(row)
        print(f"FP32 {row['width']:4} {row['fixture']:16} accepted={row['acceptedPercent']:6.2f}% "
              f"baseline={b:.2f} candidate={c:.2f} reduction={row['reductionPercent']:.2f}%")

(root / "summary.json").write_text(json.dumps(dict(cost=cost, fp32=experiments), indent=2) + "\n")
