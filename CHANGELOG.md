# Changelog

## [0.8.0](https://github.com/ooples/AiDotNet.Evolution/compare/v0.7.0...v0.8.0) (2026-10-31)


### ⚠ BREAKING CHANGES

* **bench:** the CLI's program-length axis now adapts to the observed lengths by default. Set "binning": "Linear" on the length descriptor for the old fixed range. Imported OpenEvolve feature dimensions are adaptive min-max.
* **programs:** ProcessProgramExecutionEngine and WarmPythonExecutionEngine (WarmForkWorker and WarmReusedWorker) now throw ArgumentException at construction unless ProgramSandboxOptions.AllowUnsandboxedExecution is true, because they do not isolate candidates from the host's files, network or system calls. Set it to keep running candidates as host processes, or use ContainerExecutionEngine (ProgramSandboxMode.Container). CLI run files must now carry either a "container" section or "allowUnsandboxedExecution": true; a run file with neither is refused at load. Runs imported from an OpenEvolve config write the opt-in themselves.
* **checkpoint:** ProgramGenomeCodec implements IEvolutionUtf8GenomeCodec<ProgramGenome>, which adds a Deserialize(ReadOnlySpan<byte>) overload.
* **benchmarks:** ProfileDelayBucket gains MedianMilliseconds; profile evidence now records it.
* **checkpoint:** IEvolutionGenomeCodec.Deserialize is now called concurrently during a resume, so codecs must be thread-safe. The built-in codecs are.

### Features

* **bench:** AlphaEvolve problems as tasks both engines run ([#361](https://github.com/ooples/AiDotNet.Evolution/issues/361)) ([7428bcd](https://github.com/ooples/AiDotNet.Evolution/commit/7428bcdcbf24bd906ade7a2cc674126be73e4e03))
* **programs:** container execution engine and an explicit opt-in for unsandboxed execution ([#351](https://github.com/ooples/AiDotNet.Evolution/issues/351)) ([9854dfe](https://github.com/ooples/AiDotNet.Evolution/commit/9854dfea6371330af8ea1033c67ae6d27d97361a))
* **release:** publish npm and PyPI at the NuGet version through trusted publishing ([#357](https://github.com/ooples/AiDotNet.Evolution/issues/357)) ([c7d6668](https://github.com/ooples/AiDotNet.Evolution/commit/c7d6668e3fada20ae70050cac34fabcc30899745))


### Bug Fixes

* **benchmarks:** hold simulated delays to the clock and judge them by the median ([#353](https://github.com/ooples/AiDotNet.Evolution/issues/353)) ([acc3ea9](https://github.com/ooples/AiDotNet.Evolution/commit/acc3ea9b35152ed895e5c88df82f1ab2b0095ab0))


### Performance

* **checkpoint:** hand archived genome payloads to UTF-8 codecs as bytes on resume ([#358](https://github.com/ooples/AiDotNet.Evolution/issues/358)) ([e217774](https://github.com/ooples/AiDotNet.Evolution/commit/e217774d7afc1ebb07e7061c5797f188c6c5cb24))
* **checkpoint:** journal archives in segments; resume 10,000 elites in under 1 s ([#352](https://github.com/ooples/AiDotNet.Evolution/issues/352)) ([d07beda](https://github.com/ooples/AiDotNet.Evolution/commit/d07beda1db830ca0610f4fc8e5ce91b72b8c8298))
* **packages:** ship ReadyToRun images for six runtimes in every .NET library package ([#354](https://github.com/ooples/AiDotNet.Evolution/issues/354)) ([73e3c89](https://github.com/ooples/AiDotNet.Evolution/commit/73e3c8977c50475659ac1ce327d115bb1fdcbddc))


### Tests

* **bench:** E-4 capped pilot tooling and damped_oscillator evidence at 45 iterations ([#359](https://github.com/ooples/AiDotNet.Evolution/issues/359)) ([9e054ac](https://github.com/ooples/AiDotNet.Evolution/commit/9e054ac94af3147eeb5f8063cb2ee85ee16478b5))

## [0.7.0](https://github.com/ooples/AiDotNet.Evolution/compare/v0.6.0...v0.7.0) (2026-10-26)


### Features

* **ptx:** make the editable-line bound configurable ([#342](https://github.com/ooples/AiDotNet.Evolution/issues/342)) ([f152e12](https://github.com/ooples/AiDotNet.Evolution/commit/f152e12461f1c4e91bd10512525769f18865c993))

## [0.6.0](https://github.com/ooples/AiDotNet.Evolution/compare/v0.5.0...v0.6.0) (2026-10-19)


### Features

* **bench:** publish where v1 stands against openevolve and alphaevolve ([#316](https://github.com/ooples/AiDotNet.Evolution/issues/316)) ([6833401](https://github.com/ooples/AiDotNet.Evolution/commit/68334010804b9ad024b3a376b153a86bc0eedada))


### Bug Fixes

* **bench:** absolutise the paths the pilot hands to its subprocesses ([#330](https://github.com/ooples/AiDotNet.Evolution/issues/330)) ([be9d252](https://github.com/ooples/AiDotNet.Evolution/commit/be9d2528b054ba22662ba088b9f11cc1f78ff86a))
* **bench:** compare served model calls, not attempted ones, against the broker ([#333](https://github.com/ooples/AiDotNet.Evolution/issues/333)) ([42b456e](https://github.com/ooples/AiDotNet.Evolution/commit/42b456e8fdd929c7ad0b6c10c1b1ab8ae8a919b8)), closes [#122](https://github.com/ooples/AiDotNet.Evolution/issues/122)
* **ci:** retry release-please once when the account's api quota is exhausted ([#332](https://github.com/ooples/AiDotNet.Evolution/issues/332)) ([3845875](https://github.com/ooples/AiDotNet.Evolution/commit/3845875242c93e89b98032db8c2b794a0d94b4a4)), closes [#183](https://github.com/ooples/AiDotNet.Evolution/issues/183)

## [0.5.0](https://github.com/ooples/AiDotNet.Evolution/compare/v0.4.0...v0.5.0) (2026-10-16)


### Features

* **bench:** build the release evidence report, and the lint its metric is ([#311](https://github.com/ooples/AiDotNet.Evolution/issues/311)) ([f862513](https://github.com/ooples/AiDotNet.Evolution/commit/f862513d1af2494ecf157a25d3c7986c300afc08))
* **bench:** measure the router against the best single method it must beat ([#307](https://github.com/ooples/AiDotNet.Evolution/issues/307)) ([9ee2c99](https://github.com/ooples/AiDotNet.Evolution/commit/9ee2c99d6f7e955608065ea51728971b3140add0))
* **bench:** measure what a warm start saves, in evaluations ([#312](https://github.com/ooples/AiDotNet.Evolution/issues/312)) ([d88a5ed](https://github.com/ooples/AiDotNet.Evolution/commit/d88a5ed54f6ab6d29b1890caf78a59c2bebca1f7))
* **bench:** measure what compiler-guided repair recovers, and what it costs ([#305](https://github.com/ooples/AiDotNet.Evolution/issues/305)) ([4756182](https://github.com/ooples/AiDotNet.Evolution/commit/47561821ed4f9aecde229bf6a7944afea68475c2))
* **bench:** measure what novelty detection and experience reuse suppress ([#306](https://github.com/ooples/AiDotNet.Evolution/issues/306)) ([f5a4d12](https://github.com/ooples/AiDotNet.Evolution/commit/f5a4d121e2620282049f982f7ba6c0bb328230a5))
* **bench:** tune a consumer model with the engine against grid search at equal budget ([#310](https://github.com/ooples/AiDotNet.Evolution/issues/310)) ([dfddeaf](https://github.com/ooples/AiDotNet.Evolution/commit/dfddeaf47356d7cfee66b5e6815ad3255bf737e0))
* **eng:** diff openevolve's config against the catalog, not against prose ([#315](https://github.com/ooples/AiDotNet.Evolution/issues/315)) ([5097fef](https://github.com/ooples/AiDotNet.Evolution/commit/5097fefcf0bf936440c85041c798e4b1cd72b947))


### Refactoring

* **core:** handle nulls in the three worst offenders instead of suppressing them ([#313](https://github.com/ooples/AiDotNet.Evolution/issues/313)) ([5109363](https://github.com/ooples/AiDotNet.Evolution/commit/5109363880a1a3c07b5d3d2234719ee1fe727858))
* **core:** handle the pipeline's nulls instead of suppressing them ([#304](https://github.com/ooples/AiDotNet.Evolution/issues/304)) ([7bad10a](https://github.com/ooples/AiDotNet.Evolution/commit/7bad10a9c2efe95cf0ef2c1333749342a3d94ccc))

## [0.4.0](https://github.com/ooples/AiDotNet.Evolution/compare/v0.3.0...v0.4.0) (2026-10-16)


### Features

* **bench:** rank the cells we lose, which is v1-07's third criterion ([#302](https://github.com/ooples/AiDotNet.Evolution/issues/302)) ([e220730](https://github.com/ooples/AiDotNet.Evolution/commit/e220730af66da240299a7165f64a280a8201bd4f))
* **bench:** the execute_cell campaign_runner has never had ([#289](https://github.com/ooples/AiDotNet.Evolution/issues/289)) ([9a6c64d](https://github.com/ooples/AiDotNet.Evolution/commit/9a6c64d244a6bc26b2f52542a49ade3366dc37c9)), closes [#116](https://github.com/ooples/AiDotNet.Evolution/issues/116)
* **bench:** the live pilot runner, with the two refusals that stand before a bill ([#294](https://github.com/ooples/AiDotNet.Evolution/issues/294)) ([51d5959](https://github.com/ooples/AiDotNet.Evolution/commit/51d5959e5ac5f9b95d5ecd40699d5fd9cb3834e8))
* **bindings:** TypeScript reference worker, worker-overhead harness and CI-run upstream evaluator (V1-25) ([#239](https://github.com/ooples/AiDotNet.Evolution/issues/239)) ([f744ba5](https://github.com/ooples/AiDotNet.Evolution/commit/f744ba58e43cb3bf757225ba7970af8060fccc4b))
* both pilot arms behind one seam, each accounting for what it actually spent ([#292](https://github.com/ooples/AiDotNet.Evolution/issues/292)) ([e5b7c18](https://github.com/ooples/AiDotNet.Evolution/commit/e5b7c18e6d6d042803746d5a4979ed7e42ca2c66))
* **cli:** make a run's quality legible, and say when it is not trustworthy ([#272](https://github.com/ooples/AiDotNet.Evolution/issues/272)) ([934909c](https://github.com/ooples/AiDotNet.Evolution/commit/934909cf1ccd42bc77d07c51acfff1b720959bab)), closes [#169](https://github.com/ooples/AiDotNet.Evolution/issues/169) [#170](https://github.com/ooples/AiDotNet.Evolution/issues/170) [#42](https://github.com/ooples/AiDotNet.Evolution/issues/42)
* **cli:** notice an imported model configuration that can stall a run silently ([#267](https://github.com/ooples/AiDotNet.Evolution/issues/267)) ([5b3cbce](https://github.com/ooples/AiDotNet.Evolution/commit/5b3cbceb390243894d6c2dfcbe4b3b98039c6d0f)), closes [#169](https://github.com/ooples/AiDotNet.Evolution/issues/169) [#42](https://github.com/ooples/AiDotNet.Evolution/issues/42)
* **core:** draw each proposal's model from weights on the run's own stream ([#163](https://github.com/ooples/AiDotNet.Evolution/issues/163)) ([#231](https://github.com/ooples/AiDotNet.Evolution/issues/231)) ([444b5a2](https://github.com/ooples/AiDotNet.Evolution/commit/444b5a28376dd412253f2ca549da77eb3037126b))
* **deployment:** have the promotion sweep emit the evidence it asserts ([#284](https://github.com/ooples/AiDotNet.Evolution/issues/284)) ([14e0fbb](https://github.com/ooples/AiDotNet.Evolution/commit/14e0fbb66b64b66f1525e7e9b4700b5b6d500e17)), closes [#43](https://github.com/ooples/AiDotNet.Evolution/issues/43)
* dev-partition ablation for llm feedback, and the two defects it exposed ([#246](https://github.com/ooples/AiDotNet.Evolution/issues/246)) ([d8a6eda](https://github.com/ooples/AiDotNet.Evolution/commit/d8a6eda052fd0ffe8467e6676d2f6eb8f6231ea9))
* **eng:** add the story-checkbox audit script and its measured baseline ([#108](https://github.com/ooples/AiDotNet.Evolution/issues/108)) ([#230](https://github.com/ooples/AiDotNet.Evolution/issues/230)) ([ccb430f](https://github.com/ooples/AiDotNet.Evolution/commit/ccb430f8b8d4b6065a333240700812081214e76c))
* **engine:** have the re-measurement sweep emit the evidence it asserts ([#285](https://github.com/ooples/AiDotNet.Evolution/issues/285)) ([b193f20](https://github.com/ooples/AiDotNet.Evolution/commit/b193f20e25ab3d9af72951e6d85a523f9ca0ce70)), closes [#24](https://github.com/ooples/AiDotNet.Evolution/issues/24)
* **eng:** measure the delivery boxes instead of annotating them by hand ([#300](https://github.com/ooples/AiDotNet.Evolution/issues/300)) ([ccd64cf](https://github.com/ooples/AiDotNet.Evolution/commit/ccd64cf123b103eddc623448fc04bca31a5ad5d2)), closes [#108](https://github.com/ooples/AiDotNet.Evolution/issues/108)
* expose the judge knobs that decide whether feedback can act ([#253](https://github.com/ooples/AiDotNet.Evolution/issues/253)) ([3529e46](https://github.com/ooples/AiDotNet.Evolution/commit/3529e46372881c7951c4d7efb6dcd7a432647532))
* five v1 gaps the audits found, each measured rather than asserted ([#301](https://github.com/ooples/AiDotNet.Evolution/issues/301)) ([c5c4ecb](https://github.com/ooples/AiDotNet.Evolution/commit/c5c4ecbe020aea5f7819ed5f55569d4926a1fc5b))
* **gate:** require a 95% interval above 1.0 and a per-task regression limit, not just the mean ([#297](https://github.com/ooples/AiDotNet.Evolution/issues/297)) ([dda6bce](https://github.com/ooples/AiDotNet.Evolution/commit/dda6bcee33f0fa41a923408c4405a49f5e09ca04))
* let a caller declare the environment a sandboxed evaluator sees ([#245](https://github.com/ooples/AiDotNet.Evolution/issues/245)) ([270ca44](https://github.com/ooples/AiDotNet.Evolution/commit/270ca4407958d99319b8ae07314416f5232b21b2))
* measure whether the proposer can solve the ablation task at all ([#256](https://github.com/ooples/AiDotNet.Evolution/issues/256)) ([9e44328](https://github.com/ooples/AiDotNet.Evolution/commit/9e44328183ff0e0071792545483ad7f1de2830cf))
* one task both pilot arms can run, with the held-out split kept out of selection ([#288](https://github.com/ooples/AiDotNet.Evolution/issues/288)) ([0570c8c](https://github.com/ooples/AiDotNet.Evolution/commit/0570c8ca004207dedc6c206d0f0c47e862d45786))
* paired ablation for meta-prompt evolution, and the two gaps it exposed ([#249](https://github.com/ooples/AiDotNet.Evolution/issues/249)) ([d41d8e1](https://github.com/ooples/AiDotNet.Evolution/commit/d41d8e1c37026322056443ea7e464666ff1d66a5))
* **programs:** evolve the system instructions alongside the programs ([#166](https://github.com/ooples/AiDotNet.Evolution/issues/166)) ([#233](https://github.com/ooples/AiDotNet.Evolution/issues/233)) ([818b642](https://github.com/ooples/AiDotNet.Evolution/commit/818b642affbc8e46318ccbde6612fc15e4380374))
* **ptx:** evolve PTX kernel source with verified correctness and timing ([#218](https://github.com/ooples/AiDotNet.Evolution/issues/218)) ([ca96524](https://github.com/ooples/AiDotNet.Evolution/commit/ca965243c6fb38f4d3353cf54315d9d7541d3966))
* **release:** port the US-12 release claim gate to main ([#240](https://github.com/ooples/AiDotNet.Evolution/issues/240)) ([65ced88](https://github.com/ooples/AiDotNet.Evolution/commit/65ced8823770bdd7b1901f0a5650c62443e9a5d5))
* seal a held-out split, score it independently, and measure whether the family can carry a claim ([#282](https://github.com/ooples/AiDotNet.Evolution/issues/282)) ([1b3c9fb](https://github.com/ooples/AiDotNet.Evolution/commit/1b3c9fbceaf0b42f5017782c5054f714bce30f2c))
* ship the observer that credits an evolving instruction ([#250](https://github.com/ooples/AiDotNet.Evolution/issues/250)) ([6fb9d24](https://github.com/ooples/AiDotNet.Evolution/commit/6fb9d2430e87767fda83d29ac32ffcd1af46a74d))


### Bug Fixes

* **bench:** run the declared track panel instead of silently running all six ([#287](https://github.com/ooples/AiDotNet.Evolution/issues/287)) ([9189f22](https://github.com/ooples/AiDotNet.Evolution/commit/9189f22b6b0c650f780bd37634249ea876e46248)), closes [#286](https://github.com/ooples/AiDotNet.Evolution/issues/286)
* **ci:** stop each merge to main cancelling the previous merge's run ([#299](https://github.com/ooples/AiDotNet.Evolution/issues/299)) ([a875c8d](https://github.com/ooples/AiDotNet.Evolution/commit/a875c8d78128eab04417f729d17eb4df6ca514e0)), closes [#108](https://github.com/ooples/AiDotNet.Evolution/issues/108)
* **engine:** refuse a declared constraint violation in the default archive too ([#259](https://github.com/ooples/AiDotNet.Evolution/issues/259)) ([0a1a561](https://github.com/ooples/AiDotNet.Evolution/commit/0a1a56103f310737ceaa9dce396d8c54aa7c45b2)), closes [#21](https://github.com/ooples/AiDotNet.Evolution/issues/21) [#108](https://github.com/ooples/AiDotNet.Evolution/issues/108)
* **evidence:** stop an unrelated merge turning main red, and refresh the census ([#277](https://github.com/ooples/AiDotNet.Evolution/issues/277)) ([1c68d2c](https://github.com/ooples/AiDotNet.Evolution/commit/1c68d2ce00472a1a097999240a2f0d5e73c0239f)), closes [#22](https://github.com/ooples/AiDotNet.Evolution/issues/22)
* **execution:** report the address-space backstop's kill as a memory violation, not a generic failure ([#235](https://github.com/ooples/AiDotNet.Evolution/issues/235)) ([b84f675](https://github.com/ooples/AiDotNet.Evolution/commit/b84f6750a47669737ca7eaef275ed70091f516a8))
* hand the openevolve evaluator a path a python literal can hold ([#247](https://github.com/ooples/AiDotNet.Evolution/issues/247)) ([55010f7](https://github.com/ooples/AiDotNet.Evolution/commit/55010f749985d4081a375b8d04bdddd48a976c0e))
* make a failing evaluator's own output reachable from the run report ([#258](https://github.com/ooples/AiDotNet.Evolution/issues/258)) ([1bae398](https://github.com/ooples/AiDotNet.Evolution/commit/1bae398b38928d43ddd635ffd8bea0bb2c9058aa))
* **programs:** stop the proposal options clone from dropping the declared descriptors ([#243](https://github.com/ooples/AiDotNet.Evolution/issues/243)) ([3adaec0](https://github.com/ooples/AiDotNet.Evolution/commit/3adaec0e9cae50026d370fbef1979a24c2251a29))


### Performance

* **work:** append-only group-committed journal for durable delivery ([#241](https://github.com/ooples/AiDotNet.Evolution/issues/241)) ([4cda0c9](https://github.com/ooples/AiDotNet.Evolution/commit/4cda0c942bfd90b88cfb598e0a77543ac9460e5c))


### Documentation

* claude_code_quickstart runs unmodified with the real provider ([#257](https://github.com/ooples/AiDotNet.Evolution/issues/257)) ([b791d8b](https://github.com/ooples/AiDotNet.Evolution/commit/b791d8b61545fc3fa72f37a49c22e4a0a862ec1b))
* correct the judge-score derivation, which used the wrong blend coefficient ([#255](https://github.com/ooples/AiDotNet.Evolution/issues/255)) ([568f356](https://github.com/ooples/AiDotNet.Evolution/commit/568f356af6306d0bf7e4a58c7757b719b216b40c))
* **engine:** warn where the capacity is chosen, not only where it is tested ([#273](https://github.com/ooples/AiDotNet.Evolution/issues/273)) ([5e94baa](https://github.com/ooples/AiDotNet.Evolution/commit/5e94baad87b8d92b17e9dd211be0a03f3dca1cc6)), closes [#24](https://github.com/ooples/AiDotNet.Evolution/issues/24)
* **evidence:** audit the parity stories, where 21 of 23 are closed with unticked criteria ([#298](https://github.com/ooples/AiDotNet.Evolution/issues/298)) ([0424022](https://github.com/ooples/AiDotNet.Evolution/commit/0424022c49342a9567b376b9b16ae86ff9befe49)), closes [#108](https://github.com/ooples/AiDotNet.Evolution/issues/108)
* **evidence:** re-measure our overhead column at main, meeting the last V1-70 target ([#175](https://github.com/ooples/AiDotNet.Evolution/issues/175)) ([#242](https://github.com/ooples/AiDotNet.Evolution/issues/242)) ([de05cb5](https://github.com/ooples/AiDotNet.Evolution/commit/de05cb53f6662e86ba9c8bbefb17acb391d13818))
* **evidence:** re-measure the story-checkbox audit, and what the fall from 90 is not ([#260](https://github.com/ooples/AiDotNet.Evolution/issues/260)) ([e8f21c9](https://github.com/ooples/AiDotNet.Evolution/commit/e8f21c9e4eeeee20c9b7960af1e4cee172f2a6e7)), closes [#108](https://github.com/ooples/AiDotNet.Evolution/issues/108)
* **evidence:** record the audit after the merges, 72 to 68 ([#281](https://github.com/ooples/AiDotNet.Evolution/issues/281)) ([03e6114](https://github.com/ooples/AiDotNet.Evolution/commit/03e611499a02f5404781c29179b850d143ca2f0c)), closes [#108](https://github.com/ooples/AiDotNet.Evolution/issues/108)
* **evidence:** record the distributed run V1-61 asks for, measured ([#174](https://github.com/ooples/AiDotNet.Evolution/issues/174)) ([#238](https://github.com/ooples/AiDotNet.Evolution/issues/238)) ([c458617](https://github.com/ooples/AiDotNet.Evolution/commit/c458617fd8c586eb9e2cee3b12d0dbbc723e3231))
* **evidence:** record what process isolation costs per evaluation ([#181](https://github.com/ooples/AiDotNet.Evolution/issues/181)) ([#229](https://github.com/ooples/AiDotNet.Evolution/issues/229)) ([f0b1aee](https://github.com/ooples/AiDotNet.Evolution/commit/f0b1aee9837032be3f286e222751455a8909bc43))
* **evidence:** the V1-01 checkbox audit at 0873a7d, 68 to 66 ([#295](https://github.com/ooples/AiDotNet.Evolution/issues/295)) ([0f8cc16](https://github.com/ooples/AiDotNet.Evolution/commit/0f8cc16c5776b3bd7fed358a1c4d9529b16e9cd8))
* give the readme a path to a first report, and time it on a clean machine ([#275](https://github.com/ooples/AiDotNet.Evolution/issues/275)) ([b8996a4](https://github.com/ooples/AiDotNet.Evolution/commit/b8996a4e1ea334cf1c5ce6a7b278360c91e5c1bc))
* **parity:** document each example's real-run command, and measure the claim about it ([#296](https://github.com/ooples/AiDotNet.Evolution/issues/296)) ([e981bcf](https://github.com/ooples/AiDotNet.Evolution/commit/e981bcf76ceb59a65ecf0022db848bf5ad15bab0)), closes [#170](https://github.com/ooples/AiDotNet.Evolution/issues/170)
* **parity:** record what the artifacts example needs, without unsmoking it ([#244](https://github.com/ooples/AiDotNet.Evolution/issues/244)) ([0c3a15a](https://github.com/ooples/AiDotNet.Evolution/commit/0c3a15a9dee565ad74bfcd465578d0182058e238))
* record what V1-01's 72 open boxes actually are ([#266](https://github.com/ooples/AiDotNet.Evolution/issues/266)) ([0c6faf3](https://github.com/ooples/AiDotNet.Evolution/commit/0c6faf370859947f7c6a791c4d46c466847090b7))
* two more real-provider runs, and what each one actually optimised ([#265](https://github.com/ooples/AiDotNet.Evolution/issues/265)) ([ed3e786](https://github.com/ooples/AiDotNet.Evolution/commit/ed3e78659d192d2a021f67df5698f06443d13b38))

## [0.3.0](https://github.com/ooples/AiDotNet.Evolution/compare/v0.2.0...v0.3.0) (2026-10-13)


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
* ship net462 and netstandard2.0 assets for AiDotNet.Evolution and Surrogates ([#224](https://github.com/ooples/AiDotNet.Evolution/issues/224)) ([29917c4](https://github.com/ooples/AiDotNet.Evolution/commit/29917c4e76e61c9b256a22c923d2b09a43a84d04))


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
