# V1-61 (#174): distributed evaluation across containers, measured

`benchmarks/EvolutionDistributed/verify.py` reports **all five checks passing**. Raw rows:
`summary.json`, plus `single.json`, `multi.json`, `tp1.json`, `tp4.json` and the two logs from the
kill scenario.

## Method

`benchmarks/EvolutionDistributed/run-containers.sh <publish-dir> <out>` with `EVALUATIONS=40`, image
`mcr.microsoft.com/dotnet/runtime:10.0`. Four scenarios:

| run | shape |
| --- | --- |
| `single` | one container: coordinator with one in-process worker, the single-host baseline |
| `multi` | a coordinator plus three worker containers; `w1` is killed while holding a lease |
| `tp1` | throughput, one remote worker, 1 s evaluator, continuous dispatch |
| `tp4` | throughput, four remote workers, same evaluator |

Each container has its own network namespace and the workers reach the coordinator only over the
container network. The share volume carries the bootstrap file (certificate fingerprint and
compatibility hash), mounted read-only on the workers.

## Results

| check | result | evidence |
| --- | --- | --- |
| multi-host final state hash equals single-host | **pass** | both `464c4c45e20122165008b1400616ebb7d9c9bea90c814b260642b321b9d0abb0` |
| state hash independent of worker count | **pass** | one and four workers both `4cfb9f694aae1274c32ae53470023723e68fd37361222cdccddc605b5c02cbd4` |
| every evaluation committed exactly once | **pass** | asked 31, delivered 31, missing 0, 31 with an accepted commit, none accepted twice |
| killed worker's lease re-issued | **pass** | `w1`'s last claim was evaluation 2 under lease `21edd80c…`; the final commit for it came from `worker:w2` under lease `6f8b2c40…` |
| four remote workers ≥ 3.5× one | **pass** | 0.956 → 3.637 evaluations/s, **3.80×**, 95.1% efficiency |

## What this run is not

Stated here rather than left for a reader to assume:

- **Not the reference machine.** Windows 11 with Docker Desktop, not the Linux reference host the
  metrics table names. The scaling figure in particular is machine-dependent; the state-hash and
  exactly-once checks are not.
- **40 evaluations, not the harness default of 80.** Enough for the determinism and exactly-once
  checks, which compare complete runs; a longer run would tighten the throughput figure.
- **Containers on one host, not two hosts.** The criterion permits "containers with separate network
  namespaces", which this is, but it does not exercise a real network between physical machines.
- **One repeat per scenario.** The three correctness checks are deterministic comparisons and do not
  need repeats; the 3.80× is a single measurement, and the harness's own design note explains why
  the kill scenario uses batch rather than continuous dispatch.

## Reproducing

```bash
dotnet publish benchmarks/EvolutionDistributed/EvolutionDistributed.csproj -c Release -o /tmp/dist-app
EVALUATIONS=40 bash benchmarks/EvolutionDistributed/run-containers.sh /tmp/dist-app /tmp/dist-out
python benchmarks/EvolutionDistributed/verify.py /tmp/dist-out
```

From Git Bash on Windows, set `TMPDIR` to a path under the user profile and `MSYS_NO_PATHCONV=1`
first: the harness's share directory comes from `mktemp -d`, and Docker Desktop cannot bind-mount
the MSYS `/tmp` path, which makes the coordinator's `result.json` never appear on the host.
