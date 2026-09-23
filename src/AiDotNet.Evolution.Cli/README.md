# AiDotNet.Evolution.Cli

`aidotnet-evolve` is a `dotnet tool` for AiDotNet.Evolution runs.

```text
dotnet tool install --global AiDotNet.Evolution.Cli --prerelease

aidotnet-evolve inspect <trace>
aidotnet-evolve compare <traceA> <traceB>
aidotnet-evolve export  <trace> <output-directory>
aidotnet-evolve report  <trace> <output.html>
```

Traces are the JSONL files written by `EvolutionTraceObserver`. `report` writes a self-contained HTML page (no scripts,
no external requests) with the lineage, archive heatmap, and progress and cost curves. `export` and `report` never
overwrite an existing file. Exit code `2` means a usage or input error; the message is on standard error.