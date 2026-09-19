# Changelog

## [0.1.1](https://github.com/ooples/AiDotNet.Evolution/compare/v0.1.0...v0.1.1) (2026-09-19)


### Bug Fixes

* **release:** publish all Evolution packages and validate before tagging ([#104](https://github.com/ooples/AiDotNet.Evolution/issues/104)) ([7de3011](https://github.com/ooples/AiDotNet.Evolution/commit/7de30119328557b8e04b937620fd1b06eefda56d))

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
