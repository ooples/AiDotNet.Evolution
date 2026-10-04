# Changelog

## [1.0.0](https://github.com/ooples/AiDotNet.Evolution/compare/v0.2.0...v1.0.0) (2026-10-04)


### ⚠ BREAKING CHANGES

* EvolutionDeploymentDecision.Outcome is EvolutionDeploymentOutcome, not string.

### Features

* classify the v1 public api and enforce it (V1-82) ([#206](https://github.com/ooples/AiDotNet.Evolution/issues/206)) ([9cfb696](https://github.com/ooples/AiDotNet.Evolution/commit/9cfb6963b4c66a2360a35434f863c78cdd93c338))
* **cli:** a live, self-refreshing run report (V1-59) ([#196](https://github.com/ooples/AiDotNet.Evolution/issues/196)) ([95d85ba](https://github.com/ooples/AiDotNet.Evolution/commit/95d85bae01e5dc476d692d457b1fb59bc2fe2777)), closes [#172](https://github.com/ooples/AiDotNet.Evolution/issues/172)
* **cli:** run openevolve configs and evaluators unmodified (V1-56) ([#207](https://github.com/ooples/AiDotNet.Evolution/issues/207)) ([de8810c](https://github.com/ooples/AiDotNet.Evolution/commit/de8810cbef588beb39bb01da6b39b272807e27ea))
* enforced resource limits and multi-host distributed evaluation (V1-61) ([#205](https://github.com/ooples/AiDotNet.Evolution/issues/205)) ([d220e82](https://github.com/ooples/AiDotNet.Evolution/commit/d220e82971ca06be521522c9c5f72591af5fbe9f))
* **engine:** drive deadlines and delays from a replaceable clock (V1-80) ([#190](https://github.com/ooples/AiDotNet.Evolution/issues/190)) ([ac1c68e](https://github.com/ooples/AiDotNet.Evolution/commit/ac1c68e657a4feb244141db656c673a2452682dd))
* **engine:** store large and binary artifacts in full (V1-58) ([#188](https://github.com/ooples/AiDotNet.Evolution/issues/188)) ([5b1fbb8](https://github.com/ooples/AiDotNet.Evolution/commit/5b1fbb8e3a4bab65e0905bd43059ff19012d840d))
* **programs:** feed experience lessons into proposals (V1-52) ([#194](https://github.com/ooples/AiDotNet.Evolution/issues/194)) ([58cfe7b](https://github.com/ooples/AiDotNet.Evolution/commit/58cfe7bbf29f73af3568959355210bb7fb1f418c))
* **programs:** model provider parity with openevolve (V1-51) ([#189](https://github.com/ooples/AiDotNet.Evolution/issues/189)) ([1396ee0](https://github.com/ooples/AiDotNet.Evolution/commit/1396ee01b384306e995a3446c4d0e7447d431ddf)), closes [#164](https://github.com/ooples/AiDotNet.Evolution/issues/164)
* **programs:** promote metrics and the score to archive descriptors (V1-55) ([#186](https://github.com/ooples/AiDotNet.Evolution/issues/186)) ([feefca0](https://github.com/ooples/AiDotNet.Evolution/commit/feefca0eb2179916bf2ac175377ed43e461664ea))
* **programs:** weighted model ensembles (V1-50) ([#195](https://github.com/ooples/AiDotNet.Evolution/issues/195)) ([389c735](https://github.com/ooples/AiDotNet.Evolution/commit/389c7350fbcd6b7ca63091a3b34edb27cc6be777))


### Bug Fixes

* resume a budget-truncated batch exactly by carrying its evaluator calls ([#210](https://github.com/ooples/AiDotNet.Evolution/issues/210)) ([b813204](https://github.com/ooples/AiDotNet.Evolution/commit/b81320415077545fa83ce1b8845947bdcc35a0d6))


### Performance

* **checkpoints:** segmented, streamed checkpoints for long runs (V1-73) ([#209](https://github.com/ooples/AiDotNet.Evolution/issues/209)) ([d821729](https://github.com/ooples/AiDotNet.Evolution/commit/d82172967f83dd62b7271db0b2f3008cecbb3cba))
* **engine:** cheaper checkpoint saves, and checkpoints beyond 16 MiB (V1-74) ([#198](https://github.com/ooples/AiDotNet.Evolution/issues/198)) ([08ac70b](https://github.com/ooples/AiDotNet.Evolution/commit/08ac70b11ab025da9db0e4facae299017f079868))
* **engine:** keep a slow model busy with overlapping proposals (V1-75) ([#199](https://github.com/ooples/AiDotNet.Evolution/issues/199)) ([9feb16d](https://github.com/ooples/AiDotNet.Evolution/commit/9feb16d08da94c550ced5af5447211a39abdcc04))
* **engine:** keep per-evaluation cost flat as the archive grows (V1-72) ([#201](https://github.com/ooples/AiDotNet.Evolution/issues/201)) ([0de1a75](https://github.com/ooples/AiDotNet.Evolution/commit/0de1a751838b53ccc3225039239678b75e5cc1be))
* **engine:** stop rebuilding search genomes on every validation (V1-71) ([#197](https://github.com/ooples/AiDotNet.Evolution/issues/197)) ([9e27171](https://github.com/ooples/AiDotNet.Evolution/commit/9e2717170a2cb0e6241ab1234bc233206f03fe46))
* **engine:** valid overhead intervals against openevolve, and a state hash that stops pinning memory (V1-70) ([#200](https://github.com/ooples/AiDotNet.Evolution/issues/200)) ([65ef0d2](https://github.com/ooples/AiDotNet.Evolution/commit/65ef0d2b7f295aea6215c52fd53080bfad210f08))
* **programs:** sandbox overhead and warm python workers (V1-76) ([#208](https://github.com/ooples/AiDotNet.Evolution/issues/208)) ([59bd34b](https://github.com/ooples/AiDotNet.Evolution/commit/59bd34b80a498789ace9855273fdb0292c1d32b4))

## [0.2.0](https://github.com/ooples/AiDotNet.Evolution/compare/v0.1.0...v0.2.0) (2026-09-28)


### ⚠ BREAKING CHANGES

* **engine:** zero-latency pipeline, proposal identity v3 and Dispatch=Auto (V1-20) ([#138](https://github.com/ooples/AiDotNet.Evolution/issues/138))

### Features

* **analysis:** hash-frozen campaign pre-registration and reproducible report (V1-05) ([#130](https://github.com/ooples/AiDotNet.Evolution/issues/130)) ([726446b](https://github.com/ooples/AiDotNet.Evolution/commit/726446b5ce3030fb52aab1cc222d9ce9d712ad6f))
* **api:** classify, declare and enforce the public API (V1-11) ([#137](https://github.com/ooples/AiDotNet.Evolution/issues/137)) ([602360f](https://github.com/ooples/AiDotNet.Evolution/commit/602360f4389572ae59f15e8c10e11c8c26a84d4c))
* **benchmarks:** add oracle-checked GPU consumer proof for US-11 ([#106](https://github.com/ooples/AiDotNet.Evolution/issues/106)) ([6d26bda](https://github.com/ooples/AiDotNet.Evolution/commit/6d26bda5e690a2e9a9c068357ed44d1301204b0d))
* **benchmarks:** AlgoTune family with sealed test inputs and oracle mutants (V1-04b) ([#132](https://github.com/ooples/AiDotNet.Evolution/issues/132)) ([9837360](https://github.com/ooples/AiDotNet.Evolution/commit/9837360b1bf0514ce05d68874490f687b76dee72))
* **benchmarks:** AlphaEvolve math family with independent verifiers (V1-04a) ([#133](https://github.com/ooples/AiDotNet.Evolution/issues/133)) ([40523be](https://github.com/ooples/AiDotNet.Evolution/commit/40523be8f76058cf2b6318140ce72b0e58b754d4))
* **benchmarks:** Claude transport, OpenEvolve shim and campaign runner (V1-02, V1-03, V1-06) ([#128](https://github.com/ooples/AiDotNet.Evolution/issues/128)) ([7662afc](https://github.com/ooples/AiDotNet.Evolution/commit/7662afca614696298d92bb2f9927cac42f7a70b8))
* **benchmarks:** engine overhead head-to-head vs OpenEvolve (V1-30) ([#143](https://github.com/ooples/AiDotNet.Evolution/issues/143)) ([3eb1dd1](https://github.com/ooples/AiDotNet.Evolution/commit/3eb1dd10573233910eb5ad2364c4c7f83171a190))
* **benchmarks:** GPU/CPU kernel family with an FP64 host oracle (V1-04c) ([#134](https://github.com/ooples/AiDotNet.Evolution/issues/134)) ([46e1141](https://github.com/ooples/AiDotNet.Evolution/commit/46e1141859bf8c8d8709343ec142114e9790d34e))
* **cli:** aidotnet-evolve with reports, run/resume, lifecycle and warm start (V1-24, V1-26) ([#142](https://github.com/ooples/AiDotNet.Evolution/issues/142)) ([4fc12bd](https://github.com/ooples/AiDotNet.Evolution/commit/4fc12bddab4356a31eaad2124831f391ac0d40cb))
* **conformance:** durable-worker scenarios, tested wheel and reference Python worker (V1-25) ([#144](https://github.com/ooples/AiDotNet.Evolution/issues/144)) ([0c71e2d](https://github.com/ooples/AiDotNet.Evolution/commit/0c71e2d9d0ad8bb0add4685b1de32ffdb8ee2ab2))
* **engine:** zero-latency pipeline, proposal identity v3 and Dispatch=Auto (V1-20) ([#138](https://github.com/ooples/AiDotNet.Evolution/issues/138)) ([78fca33](https://github.com/ooples/AiDotNet.Evolution/commit/78fca331a0ddffc2eed9b571fe23277352929221))
* **programs:** bounded experience memory and advisory novelty (V1-23) ([#141](https://github.com/ooples/AiDotNet.Evolution/issues/141)) ([30471be](https://github.com/ooples/AiDotNet.Evolution/commit/30471be05e820872033fe52721b4eacb274c9886))
* **programs:** compiler-guided repair for Python tasks (V1-22) ([#140](https://github.com/ooples/AiDotNet.Evolution/issues/140)) ([c0c7cd1](https://github.com/ooples/AiDotNet.Evolution/commit/c0c7cd158c66a252a648d80368af514c6ca6b538))
* **routing:** escalation ladder and explicit stale-estimate handling (V1-21) ([#139](https://github.com/ooples/AiDotNet.Evolution/issues/139)) ([fe6ee63](https://github.com/ooples/AiDotNet.Evolution/commit/fe6ee63837757f87bffa6c14cd1301e31184d8be))


### Bug Fixes

* **api:** declare the public api merged alongside the api freeze ([#161](https://github.com/ooples/AiDotNet.Evolution/issues/161)) ([e82469c](https://github.com/ooples/AiDotNet.Evolution/commit/e82469cac845591a236063a348fde42fb84e7685))
* **build:** keep aidotnet 0.233 generators out of consuming projects ([#160](https://github.com/ooples/AiDotNet.Evolution/issues/160)) ([9235c5b](https://github.com/ooples/AiDotNet.Evolution/commit/9235c5b847658e6b22a0e821f452ab74d24a1586))
* **build:** Windows solution restore and format gate (V1-00, [#126](https://github.com/ooples/AiDotNet.Evolution/issues/126)) ([#136](https://github.com/ooples/AiDotNet.Evolution/issues/136)) ([91b0b3c](https://github.com/ooples/AiDotNet.Evolution/commit/91b0b3c61d74edaddc5a859a28f3b56eadc0c883))
* **policies:** start a trial's deadline when the trial starts, not when it is queued ([#151](https://github.com/ooples/AiDotNet.Evolution/issues/151)) ([c07d67b](https://github.com/ooples/AiDotNet.Evolution/commit/c07d67b43bc205165dc82fee9b8d7c87e8f8d6e6))
* **release:** publish all Evolution packages and validate before tagging ([#104](https://github.com/ooples/AiDotNet.Evolution/issues/104)) ([7de3011](https://github.com/ooples/AiDotNet.Evolution/commit/7de30119328557b8e04b937620fd1b06eefda56d))
* **sandbox:** measure warm resources on cgroup-v1 hosts, and audit v1 story issues ([#127](https://github.com/ooples/AiDotNet.Evolution/issues/127)) ([5e131ce](https://github.com/ooples/AiDotNet.Evolution/commit/5e131cee21159b92e7e2356473e7ee94535bb4fd))


### Refactoring

* **programs:** retire Programs/Legacy into responsibility folders (V1-10) ([#135](https://github.com/ooples/AiDotNet.Evolution/issues/135)) ([7458a9f](https://github.com/ooples/AiDotNet.Evolution/commit/7458a9fac853d4e48a8c1b70e534241ea5567e46))


### Documentation

* install the stable packages without --prerelease ([#162](https://github.com/ooples/AiDotNet.Evolution/issues/162)) ([9e5f116](https://github.com/ooples/AiDotNet.Evolution/commit/9e5f1169bbea6221ddbd6dabe557f7062b89812d))

## [0.1.0](https://github.com/ooples/AiDotNet.Evolution/compare/v0.1.0-preview.2...v0.1.0) (2026-09-16)


### Features

* **core:** add adaptive variation and reproducible quality experiments ([#15](https://github.com/ooples/AiDotNet.Evolution/issues/15)) ([7d56ab6](https://github.com/ooples/AiDotNet.Evolution/commit/7d56ab64e59b2cf30ff811ad601dbc417bebcc8e))
* drive evolution from the outside, with a NativeAOT host and a TypeScript binding ([#14](https://github.com/ooples/AiDotNet.Evolution/issues/14)) ([bdbefbf](https://github.com/ooples/AiDotNet.Evolution/commit/bdbefbf686fcfd69d0431672a78f770fdd503c37))


### Bug Fixes

* **release:** graduate Evolution to stable publishing ([#18](https://github.com/ooples/AiDotNet.Evolution/issues/18)) ([f79f3e3](https://github.com/ooples/AiDotNet.Evolution/commit/f79f3e3425e1db0ec3d25c051f7f6ec05735ae68))

## [0.1.0-preview.2](https://github.com/ooples/AiDotNet.Evolution/compare/v0.1.0-preview.1...v0.1.0-preview.2) (2026-09-11)


### Features

* **options:** make EvolutionEngineOptions.Copy public ([#16](https://github.com/ooples/AiDotNet.Evolution/issues/16)) ([b7fbc30](https://github.com/ooples/AiDotNet.Evolution/commit/b7fbc301259994b46c79e4b57e376366f8af8b86))

## 0.1.0-preview.1 (2026-09-08)

- Extract the deterministic quality-diversity engine from AiDotNet with its original Git history.
- Provide typed task, variation, selection, refinement, archive, migration, observer, and persistence contracts.
- Support MAP-Elites, deterministic parallel evaluation, islands, migration, trace output, and checkpoint/resume.
- Target .NET 10, .NET 8, and .NET Framework 4.7.1 without depending on AiDotNet or AiDotNet.Tensors.

### Features

* introduce standalone deterministic evolution engine ([#1](https://github.com/ooples/AiDotNet.Evolution/issues/1)) ([6fab34c](https://github.com/ooples/AiDotNet.Evolution/commit/6fab34cbe973935e74cd6afd9fe37a7657cc5689))


### Bug Fixes

* **ci:** stop SonarCloud from permanently blocking Dependabot PRs ([#11](https://github.com/ooples/AiDotNet.Evolution/issues/11)) ([a924578](https://github.com/ooples/AiDotNet.Evolution/commit/a9245789ab5cd0a86824808ab30fb86256db0352))
* **release:** isolate NuGet publishing OIDC ([#13](https://github.com/ooples/AiDotNet.Evolution/issues/13)) ([17f74db](https://github.com/ooples/AiDotNet.Evolution/commit/17f74db0d1ed55d90574ccd075d0dbeeae61de39))
