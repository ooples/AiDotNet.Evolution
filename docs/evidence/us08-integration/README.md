# US-08: current Evolution integration evidence

Implementation is complete and locally verified on the US-07 #78 foundation.
Independent review, current-head hosted CI and dependency merges remain merge gates.
No AiDotNet/Tensors changes, live model calls, paid API usage or default changes.

## Implemented and adversarially checked

- Shared-ledger `EvolutionCostedPortfolio.Create` for bounded, versioned consumer
  strategies, including model backends. The scripted-model test proves shared admission,
  not live model quality.
- Typed per-commit operator/configuration/evaluation attribution, parent/archive gain,
  total proposal/evaluation cost, exploration and coordinated checkpoint replay.
- Invalid, infeasible, failed, duplicate and reused measurements earn no fresh reward.
  Unknown and overrun receipts retain charges. Telemetry failures/reentry cannot alter learning.
- The study now uses `EvolutionEngine.RunAsync`, replacing historical manually constructed
  outcomes. Uniform-mixture control separates learning benefit from access to more operators.
- Reporter checks reconcile engine outcomes, raw measured quality, final elite cells,
  cost receipts and normalized rewards. Adversarial tests corrupt identities, costs,
  endpoints, timing and reward. Consumed US-07 workloads remain unchanged.

## Verification

Final Release solution build: **0 warnings, 0 errors**.
**2,347 tests passed:** net10.0 797; net8.0 720; net471 720; Python 110.
Formatting verification and whitespace checks passed.
After the optional-field reporting correction below, all 110 Python tests passed again;
no C# source changed after the final build/test batch.

Protocol `portfolio-study-v2`: 7 methods × 3 families × (32 development + 512 confirmation)
= **11,424 runs**, **0 run failures**. Actual charged work: **294,188 proposal dispatches**
+ **435,004 objective invocations** = **729,192 units**. Each run had a 64-unit ceiling.
Inner refinement work is included. Unused resources are not silently filled with extra trials.

Measured aggregate process CPU: 117.78125 seconds; run wall time: 113.3916815 seconds.
These descriptive Windows measurements are not an equal-CPU budget, isolated-machine
performance certification or compiler/model superiority claim.

## Held-out before/after

Quality is bounded to [0,1], higher is better. Development selected **crossover** as the
static comparator in every family; it is not retrospectively replaced by a better
confirmation winner. Every entry below averages 512 paired confirmation seeds.

| Family | Static quality | Parent-adaptive | Archive-adaptive | Uniform mixture |
|---|---:|---:|---:|---:|
| Numeric | 0.551157 | 0.541421 | 0.550755 | 0.533459 |
| Expression | 0.867616 | 0.856697 | 0.870116 | 0.844114 |
| Kernel | 0.933883 | 0.933650 | 0.934262 | 0.934197 |

The full [scorecard](scorecard.json) includes before/after/delta for quality, diversity,
charged cost, proposal/objective calls, CPU, wall time and failures against BOTH controls.
For example, archive-adaptive expression quality increased by 0.002500 versus static and
0.026002 versus uniform, but neither increase passed the registered confidence threshold.
Kernel archive diversity rose from 0.351959 to 0.365112; this is not proof of runtime speedup.

**No adaptive policy passed promotion.** Twelve one-sided simultaneous Hoeffding lower
bounds allocate alpha 0.05 across 3 families × 2 policies × 2 controls. The radius is
0.146317; every lower gain is below the required +0.02. Both controls must pass and
there must be no failed runs. The correct decision is **retain existing defaults**.
This completes implementation and the validation gate, not a claim that adaptation wins.

## Preserved reporting failure and amendment

The original development search completed, but verification failed with
`KeyError: MeasurementOrigin`: property-level `JsonIgnore` omits null origin.
The validator incorrectly required that optional property. A regression now checks both
omitted and explicit-null forms. Only this optional-field read changed; no reward,
method, budget, threshold or seed was adjusted in response to results.

The original plan, consumed marker, results and failed log remain in the archive.
An explicit analysis-only amendment links the original plan/data hashes and reuses
the exact development bytes. **Development was not rerun; confirmation had not started.**
Confirmation and the final report use the amended analysis hash. Reproduction scripts
and the exact measured binaries are included.

## Artifact

[verification.zip](verification.zip), **64,804,567 bytes**.
SHA-256: `04e46b5f9e276be43c730261b4b5ecd97e15ff8e371b6c309bd6173545cf9f36`.

Amended plan SHA-256:
`00c4b75694d9d1632e25a448bc9120de2472ca9c47cd3b2799591e9af829e26e`.

Archive directories: `original/` (build/tests, first registration and failed reporting),
`amended/` (unchanged development, confirmation, scorecard, consumed claims),
`scripts/` and `binaries/`. Claims are local provenance, not a defense against an owner
rewriting the entire evidence set. Historical `../us08/` evidence is not this campaign.
