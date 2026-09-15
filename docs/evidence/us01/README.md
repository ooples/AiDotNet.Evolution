# US-01 verification evidence

Executable implementation: `04f8fd84426e5ececa826c5dbcae85a5605b825c`.
Additional net10 admission-test source: `963d0f3a5cef06075f2a23a20ff698499104c3c7` (test-only; compiled against the unchanged implementation).
Shared foundation retained: `10aa358ddc806cd5fc26afedfdeed91864fcf85d`.

`verification.zip`: **2,388,767 bytes**, SHA256
`d8aced958411f7e90b8e4122a9502e711232ae2968a2a99c8a75ff3decd2231d`.

## Final local gates

| Gate | Result | Raw receipt |
| --- | --- | --- |
| Full Release solution build | Zero warnings/errors | `us01-build-committed.log` |
| net10 full suite, including final runner admission tests | 631 passed, zero failed/skipped | `us01-admission/net10-admission.trx` |
| net8 full library suite | 608 passed, zero failed/skipped | `us01-committed/net8.0/net8.0.trx` |
| net471 full library suite | 608 passed, zero failed/skipped | `us01-committed/net471/net471.trx` |
| Coverage ratchet | 89.88% line / 75.65% branch; minima 88.80% / 73.51% | `us01-coverage-final.log`, admission Cobertura XML |
| Full solution format verification | Exit 0 | `us01-format-final-verified.log` (empty success log) |
| Python partition/provenance protocol | 17 passed | `us01-suite-committed.log` |
| Existing Python paired-analysis contracts | 17 passed | `us01-analysis-tests.log` |
| Existing numeric development harness replay | 48 runs / 1536 evaluations per run bundle; two bundles byte-identical | `us01-committed/legacy-first.json`, `legacy-second.json` |
| New three-partition smoke suite | 54 numeric runs; all 11 pinned program starts validate | `a690ddc6095a48f9b6f938f0104c123d/{development,selection,final}/report.json` |
| Registered lifecycle contract fixture | 18 selection runs; exactly 9 final runs from the three frozen methods; second final access refused without changing evidence | same directory's `registered-selection`, `registered-final`, `registered-lifecycle-contract`, `refused-repeat.log` |

The benchmark application targets net10; the net8/net471 counts are library regressions, not a claim that the benchmark CLI targets those runtimes. The six final admission cases were added after the first 625-test net10 pass, improving coverage from the narrow initial 88.93% line margin without excluding code or lowering the ratchet. No executable implementation changed after `04f8fd8`.

Final net10 test DLL SHA256: `cf759ada2b0deaa1b2376f2395d68e58e5610d807ec7a1cf19e961610eb69706`.
Benchmark DLL SHA256: `8f8f908b7e4e735f73fd442d1cde9de77979bc7451d294001e8750dae1be3d39`.
Both legacy replay bundles SHA256: `3c1ad35222c1cd07681a5e8bd11cf75148c223ed07f6c79b6069f0eddfed957a`.

## Interpretation and reproducibility

All local gates ran on Windows with the existing .NET runtimes and an isolated Python 3.14.6 environment. The raw program reports contain the exact NumPy/SciPy/NetworkX/cryptography and transitive dependency versions, CPU/platform identifiers, task/source/evaluator hashes, generation seeds, problem/solution hashes, warm-up and timed validation records. Numeric reports include the dependency manifest, actual assembly hashes, task/instance/search identities, method lists, raw quality/constraints/descriptors, resource receipts and equal-initial-population hashes.

Use the commands in [the suite guide](../../../benchmarks/suite/README.md). The committed gate used:

```powershell
dotnet build AiDotNet.Evolution.slnx -c Release --no-restore -p:GeneratePackageOnBuild=false -m:2
./eng/Test-RepresentativeSuite.ps1 -Python .local/benchmark-env/Scripts/python.exe -Upstream .local/algotune-upstream -SourceRevision 04f8fd84426e5ececa826c5dbcae85a5605b825c -NoBuild
```

Full tests used `dotnet test ... -c Release -f <target> --no-build --no-restore`, TRX logging, `DOTNET_PROCESSOR_COUNT=2`, and XPlat Code Coverage on net10. Runtime/source hashes distinguish the published fixtures from a later rebuild; declared source revisions are provenance labels, not cryptographic build attestations.

The catalogue pins AlgoTune `dff9914c10800c7a031c9e8c3d4d1c8cd1b38906` and 11 individual task Git blobs. Each exact upstream starting solver ran through its original validator, with a documented minimal base bridge and an additional nonfinite-output guard. No full AlgoTuner agent, model call or generated-program comparison ran.

**Every retained panel here is a contract fixture, not evidence of competitive superiority or untouched real-world generalization.** Published random roots belong only to these completed test fixtures and must never be reused as a fresh holdout. A real benchmark campaign needs a new registered plan, private-root custody, a configuration selected without final data, and the declared single final execution. Numeric work units are task-defined, not inferred price or comparable FLOPs; reference timings alone do not establish speedup.

## Failures retained

The initial `us01-build.log` failed with two CS7036 errors after raw evaluation fields were added to the shared `SampleRecord`: the numeric service and archive-partition observer still used the old constructor. Both now populate their actual quality, constraints, descriptors and genome identities. The repair build and legacy replay passed. No failed run was relabeled successful.

The earlier `8505de1611dc4f4299d334f6f6e8f64e` directory and `us01-results`/initial logs precede the final committed-source and expanded-admission gates; they are retained for audit. The authoritative suite fixture is `a690ddc6095a48f9b6f938f0104c123d`, and the authoritative net10 test receipt is the admission TRX above.

Hosted current-head CI, Linux execution, reviewer approval and eventual retarget/merge remain separate gates. Nothing here authorizes a merge, paid API use, publication or deployment.
