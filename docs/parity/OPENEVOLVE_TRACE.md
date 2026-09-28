# OpenEvolve parity: trace export and live view (V1-59, #172)

Every `EvolutionTraceConfig` option of OpenEvolve 0.3.2 (commit 411fb59) and its web visualiser, with the equivalent
here and the test that proves it.

| OpenEvolve | AiDotNet.Evolution equivalent | Proven by |
| --- | --- | --- |
| `enabled` / `output_path` | `EvolutionTraceOptions.Enabled` / `Path` | `EvolutionTraceTests` |
| `format: jsonl` / `json` | `EvolutionTraceFormat.JsonLines` / `Json` | `EvolutionTraceTests` |
| `format: hdf5` | not produced: JSONL converts losslessly (`pandas.read_json(lines=True).to_hdf`). A binary format would drop the per-record flush that makes a live trace readable | n/a |
| `buffer_size` | `FlushEveryRecords` | `EvolutionTraceTests` |
| `compress` | `Compress` | `EvolutionTraceTests` |
| `include_code` | not needed: traces never carry source. `export --include-source` binds the winner's program to its evidence | `CliRunTests` |
| `include_prompts` | prompts are kept by proposal provenance (`ProgramProvenanceOptions.IncludePromptText`), never in the trace | `ProposalProvenanceTests` |
| web visualiser (`scripts/visualizer.py`) | `aidotnet-evolve report` (static) and `aidotnet-evolve watch` (self-refreshing while the run is live) | `CliRunTests` |

## Differences worth knowing
- The live view is a local HTML file with no server, no script and no external request. It refreshes itself only while
  the run's marker is held and becomes a static report when the run ends.
- Traces are bounded (`MaxBytes`, `MaxRecords`) and report truncation; OpenEvolve's are not.