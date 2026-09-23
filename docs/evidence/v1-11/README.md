# V1-11: the public API is classified, declared and enforced

## Classification
| diagnostic | surface | types |
| --- | --- | --- |
| `AIDEVO001` | Surrogates: the 9 surrogate types in core, plus all 4 of the Surrogates package | 13 |
| `AIDEVO002` | Policy search, i.e. US-26 "evolution of search policies" (`Policies/`) | 18 |
| `AIDEVO003` | Centroid archives | 4 |

Every other public type is **stable**. The owning assemblies, and the tests, examples and benchmark that deliberately
exercise these surfaces, opt in with `NoWarn`; any other consumer gets a compile error. net471 uses an internal
`ExperimentalAttribute` polyfill, which the compiler honours by name.

## Declared API (`PublicAPI.Unshipped.txt`; moves to Shipped at the 1.0 tag, V1-19)
| package | declared members | of which experimental |
| --- | --- | --- |
| `AiDotNet.Evolution` | 2101 | 230 |
| `AiDotNet.Evolution.CSharp` | 117 | 0 |
| `AiDotNet.Evolution.Deployment` | 146 | 0 |
| `AiDotNet.Evolution.Programs` | 1598 | 0 |
| `AiDotNet.Evolution.Surrogates` | 38 | 38 |

`EnablePackageValidation` is on in all five packable projects, and all five pack with validation.

## Design rules RS0026/RS0027 (overloads with optional parameters)
Fixed by explicit overloads, with defaults unchanged and every call site source-compatible:
`EvolutionArchiveQuery.BestBy/TopBy` (both receivers), `AdaptiveVariationPortfolio` (4 constructors),
`EvolutionWorkProtocol`, `ProgramDiversityDescriptor` and `EvolveBlock.Extract`.

One justified suppression: `DelegateProgramFitnessEvaluator`'s two constructors take delegates of different arity, so no call can be
ambiguous. Seven call sites rely on named optional arguments that skip earlier parameters.

## Proof that an undeclared change fails the build (run, then reverted)
| change | result |
| --- | --- |
| add a public member | `error RS0016` |
| remove an existing member's declaration | `error RS0016` |
| rename a public member | `error RS0016` + `error RS0017` |
| revert | build succeeds |

## Verification
The whole solution builds, including Deployment, which needs #126's fix (merged into this branch). Tests: CSharp 858 (net10.0, net8.0);
Deployment 41 (net10.0, net8.0); Performance 86; Tests 1,565 (net10.0), 1,477 (net8.0) and 1,276 (net471). All pass.