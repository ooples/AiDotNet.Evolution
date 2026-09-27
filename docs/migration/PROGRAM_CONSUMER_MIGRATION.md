# Program consumer relocation: AiDotNet #2210 and #2212

The standalone replacement lives in AiDotNet.Evolution. Closing the old PRs means
relocated, not merged, and does not close their user stories or delete source branches.

| Original change | Standalone replacement |
|---|---|
| #2210 noise options, session, and tests | `src/AiDotNet.Evolution.Programs/Fitness/ProgramNoiseEvaluation*`, matching tests under `tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes` |
| #2210 consumer study and statistical verifier | `benchmarks/EvolutionNoise`: fresh RidgeRegression fits and sorting, no model-provider calls |
| #2212 metered adapter, portfolio, cost/provider contracts | Programs (`Variation/`, `Fitness/`) `ICostedProgramProposalSource`, `IProgramResourceLedgerProvider`, `MeteredProgramVariationOperator`, `ProgramVariationPortfolio`; existing portfolio adversarial tests |
| #2212 compiler-arm factory and proposal source | `CSharpProgramVariation.Create`, `CSharpProposalSource`, `ICSharpProposalClient`, `CSharpProgramSourceOptions`; compiler/options/proposal tests and `StandaloneCompilerPortfolioTests` |
| #2212 usage aggregation and builder wiring | `ProgramEvolution.CreateEngine` accepts the portfolio directly; integration test checks outcome commits, evaluation charges, and consumed proposal credits |
| #2212 output-verifier fixes | `benchmarks/EvolutionNoise/Verify-Output.Tests.ps1`, including explicit success exit after expected native failures |
| Both CI changes | Existing standalone compiler tests plus `.github/workflows/program-consumer.yml`, running fresh study, six corruption controls, and output refusal checks on every PR |
| Original evidence/docs | `aidotnet-pr-2210-original.zip` and `aidotnet-pr-2212-original.zip`; historical results, not new measurements |

Source tips: #2210 `db69ac7252da3cbf656ae9024cdd08427d06f159`,
#2212 `66d7602c92101e5ab2bd9db8cfa7f7526fa2c75d`.
The compiler's block parsing dependencies are relocated too. Original BSL licensing
is retained; the CSharp package now includes both original BSL and Apache notices.

## Deliberate clean API break

No `AiModelBuilder.ConfigureCSharpProgramEvolution` forwarding API is introduced.
Create a caller-owned `ICSharpProposalClient`, source/compiler options, and shared
`ProgramEvolutionResourceOptions`; pass the result of `CSharpProgramVariation.Create`
to `ProgramVariationPortfolio` and `ProgramEvolution.CreateEngine`.
The client contract carries bounded request options and explicit token receipts.
Neither construction nor tests infer credentials or invoke a paid provider.

Only the benchmark references AiDotNet, pinned to 0.231.0 and assembly-aliased for
model primitives. Evolution runtime and compiler projects have no AiDotNet package
dependency. This avoids silently testing the old embedded Evolution implementation.

## Verification contract

Final receipts belong under `TestResults/relocation`; a fresh consumer report must
pass `Verify-Study.ps1`, all six corrupt variants must fail verification, and an
existing report/unknown CLI argument must be rejected without overwriting evidence.
Compiler tests cover malformed edits, protected text, failed compilation/repair,
budget refusal, provider errors, absent usage, cancellation, checkpoint corruption,
and exact proposal accounting. Factory integration covers the current engine.

This port does not replace #2148, #2168, #2202, or #2203. Those remain separate
cleanup obligations, followed by the clean-breaking AiDotNet API removal PR.
