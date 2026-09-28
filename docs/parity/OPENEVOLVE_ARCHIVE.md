# OpenEvolve parity: archive and descriptors (V1-55, #168)

Every `DatabaseConfig` option of OpenEvolve 0.3.2 (commit 411fb59) that shapes the archive, with its equivalent here and
the test that proves it. OpenEvolve defects D1, D3, D4 and D7 come from the v1 readiness analysis, where each was
reproduced against the pinned package.

| OpenEvolve option | AiDotNet.Evolution equivalent | Proven by |
| --- | --- | --- |
| `feature_dimensions: [complexity]` | `ProgramLengthDescriptor` (or `ProgramTokenComplexityDescriptor`) in a `ProgramDescriptorSet` | `ProgramDescriptorTests` |
| `feature_dimensions: [diversity]` | `ProgramDiversityDescriptor` against fixed reference sources | `ProgramDescriptorTests` |
| `feature_dimensions: [score]` | `EvolutionEngineOptions.QualityDescriptorName` (existing, engine-wide) | `EvolutionCoreParityTests` |
| `feature_dimensions: [<any metric>]` | `ProgramTaskOptions.MetricDescriptors`; a missing metric is refused, as OpenEvolve raises | `ProgramDescriptorPromotionTests` |
| `feature_bins` (int or per-dimension dict) | `EvolutionDescriptorDefinition(name, min, max, bins)` per dimension, used exactly | `ArchiveDefectClassTests.D7_*` |
| `diversity_reference_size` | size of the reference set given to `ProgramDiversityDescriptor` | `ProgramDescriptorTests` |
| `archive_size` / `population_size` | `MapElitesArchive` capacity (grid-sized by default) | `MapElitesArchiveTests` |
| `elite_selection_ratio`, `exploration_ratio`, `exploitation_ratio` | `EvolutionSelectionOptions` with `RatioEvolutionSelectionPolicy` | `EvolutionCoreParityTests` |
| adaptive min-max scaling of feature values | `EvolutionDescriptorCalibrator`: ranges fixed after a declared calibration phase, never re-binned silently | `EvolutionDescriptorCalibrationTests`, `ArchiveDefectClassTests.D4_*` |

## Defect classes this archive cannot exhibit

| OpenEvolve defect | Why not here | Proven by |
| --- | --- | --- |
| D1: a NaN `combined_score` freezes its cell | a completed result must carry a finite quality | `ArchiveDefectClassTests.D1_*` |
| D3: a program that loses its cell still joins the island | a losing insertion returns `NotImproved` and adds no member | `ArchiveDefectClassTests.D3_*` |
| D4: cells drift as running min/max widen | bin geometry is fixed at construction | `ArchiveDefectClassTests.D4_*` |
| D7: requested bins silently raised to `archive_size^(1/dims)` | the grid is exactly the requested bins | `ArchiveDefectClassTests.D7_*` |
