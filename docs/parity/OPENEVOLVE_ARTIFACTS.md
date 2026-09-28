# OpenEvolve parity: evaluator artifacts (V1-58, #171)

Every artifact option of OpenEvolve 0.3.2 (commit 411fb59), with its equivalent here and the test that proves it.

| OpenEvolve option | AiDotNet.Evolution equivalent | Proven by |
| --- | --- | --- |
| `evaluator.enable_artifacts` | `EvolutionArtifactOptions.Enabled` | `EvolutionEvaluationParityTests` |
| `EvaluationResult.artifacts` (text) | `EvolutionArtifact(key, text)` on `EvolutionTaskResult` | `EvolutionEvaluationParityTests` |
| `EvaluationResult.artifacts` (bytes) | `EvolutionArtifact.FromBytes(key, content, mediaType)`, kept in full in the store | `ArtifactStoreParityTests` |
| `database.artifacts_base_path` | `EvolutionArtifactOptions.Store = new DirectoryEvolutionArtifactStore(path)` | `ArtifactStoreParityTests` |
| `database.artifact_size_threshold` (larger goes to disk) | with a store, text over `MaxArtifactBytes` is stored in full; the evaluation keeps a preview plus its `sha256:` address | `ArtifactStoreParityTests` |
| `evaluator.max_artifact_storage` | `EvolutionArtifactOptions.MaxStoredBytesPerEvaluation` (default 100 MB, as OpenEvolve) | `ArtifactStoreParityTests` |
| `database.cleanup_old_artifacts` + `artifact_retention_days` | `DirectoryEvolutionArtifactStore(path, retention)` prunes on open and via `Prune` | `ArtifactStoreParityTests` |
| timeout artifacts | `ScriptProgramEvaluationOptions.RetainArtifactText` attaches the stdout/stderr captured before a failure or timeout | `ScriptEvidenceAdversarialTests` |
| `prompt.include_artifacts` / `max_artifact_bytes` | `EvolutionArtifactOptions.DeliverToNextProposal`; `ProgramEvolutionPromptOptions.MaxArtifactBytes` | `EvolutionEvaluationParityTests`, `ProgramPromptBuilderTests` |
| `prompt.artifact_security_filter` | `EvolutionArtifactOptions.SanitizeSecrets` | `EvolutionEvaluationParityTests` |
| `database.max_snapshot_artifacts` | not needed: there is no per-iteration database snapshot | n/a |

## Differences worth knowing
- Checkpoints and the run's state hash stay text-only. Binary content, and the full body of spilled text, live only in
  the store and are referenced by content address, so a run still replays and resumes exactly.
- Stored blobs are verified against their address on every read; a tampered or truncated blob is refused.
- Failure output stays withheld unless `RetainArtifactText` is set, because it is untrusted text a candidate produced.