# Diagnostic artifact retention

After v1.51, obsolete console logs, stdout/stderr captures, verbose dispatch
journals, adapter telemetry, generated experimental HLSL dumps and duplicate
historical source snapshots were removed from the working tree. These artifacts
remain available in Git history at `v1.51` (the solution-only commit `cc18299`
also retains them).

Historical reports and validation manifests may name or hash those archived
files. Such references describe the original run; they do not imply the files
are still present in this checkout. No historical measurements were rerun or
changed during cleanup.

Retained artifacts include compact benchmark JSONL records, summary and
validation JSON, source hashes, exact viewport captures and replay inputs.
Independent numerical baselines in the regression project remain source code.

Routine diagnostic outputs are now ignored by Git. Keep future measurements
only when they support a documented comparison or serve as a reusable fixture;
prefer compact summaries over console transcripts and per-dispatch journals.
