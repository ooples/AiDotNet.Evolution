# US-24 (#42): clean machine to first report, measured

`walkthrough.json` is the raw output of three timed repeats of
[`eng/readme-walkthrough/walkthrough.sh`](../../../../eng/readme-walkthrough/walkthrough.sh), at main
`86d56c9`.

**12.155 s median against a 600 s target**, spread 12.025 to 12.399 s. Cumulative per step, on the
median run:

| step | at |
| --- | ---: |
| `dotnet new console` | 1.594 s |
| `dotnet add package AiDotNet.Evolution` | 4.851 s |
| write `Program.cs` | 4.860 s |
| `dotnet run` (200 evaluations, no model) | 9.397 s |
| `dotnet tool install --global AiDotNet.Evolution.Cli` | 11.758 s |
| `aidotnet-evolve report trace.jsonl report.html` | 11.937 s |

Deterministic across all three: **301** trace records and a **19,515-byte** report every time, and the
program's stdout matches the figures the README publishes - `best quality : -0.0111`, `depth : 7`.

## Why a container, and what "clean" means here

The metric read **unmeasured**, and the stated reason was honest: this development host already has the
SDK, Python and a prepared virtualenv, so a number taken here would describe a warm machine and flatter
the target. A container built from the README's own stated prerequisite is clean in every dimension the
walkthrough exercises:

- no NuGet package cache - `AiDotNet.Evolution` and `AiDotNet.Evolution.Cli` are restored from nuget.org
  in every repeat
- no installed `dotnet tool`
- no project and no packages
- no Python and no virtualenv
- no API key and no model endpoint; **0 model calls**, because the quickstart's task is a pure function

**What the number excludes, and it matters.** The .NET SDK is present - that is the README's
prerequisite, not part of the walkthrough - and its image layer was already pulled. Installing .NET
itself and pulling the image are therefore not in the 12.155 s. On a host with neither, add whatever
those take; the target has 588 s of headroom for them.

## What this does not measure

Not a cold-cache claim about nuget.org: the restore is a live network fetch whose time depends on the
connection, and three repeats on one connection do not bound that. The 4.851 s to `add-package` is this
machine on this day.

Not the program-evolution path either. This walkthrough never calls a model, so it times the library
quickstart and the report, not `aidotnet-evolve run` against a real provider - which needs a key, costs
money, and is what [docs/CLI.md](../../../../docs/CLI.md) documents.
