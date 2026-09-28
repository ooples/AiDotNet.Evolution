# Public API classification

Every public type in the shipped packages, classified for 1.0. `ApiClassificationTests` checks this table
against the declared public API, so a type added, removed or reclassified without updating it fails the build.

- **Stable**: covered by semantic versioning from 1.0. It has XML documentation (the compiler enforces this in
  every package), at least one test, and a guide or example that uses it. Evidence is linked.
- **Experimental**: marked `[Experimental]` with the diagnostic ID shown, so consumers must opt in. It may change
  or be removed in a minor release until the measurement named for its area exists.
- **Internal**: not part of the public surface. No public type is currently classified internal.

| Diagnostic | Area | Why it is experimental |
| --- | --- | --- |
| AIDEVO001 | Surrogate models | Screening savings are shown only under demonstration cost assumptions; no representative workload measures the effect on search quality. |
| AIDEVO002 | Policy optimisation | Its development and held-out evidence comes from small mathematical landscapes that are not claimed to be representative. |
| AIDEVO003 | Centroid archives | A caller-supplied Voronoi partition that has not been compared with the grid archive on search quality. |
| AIDEVO004 | Experience memory | Its effect on proposals needs a dev-partition ablation with real model calls. |

## AiDotNet.Evolution

| Type | Class | Evidence |
| --- | --- | --- |
| `AdaptiveIslandSearch&lt;TGenome&gt;` | Stable | [ADAPTIVE_ISLANDS.md](ADAPTIVE_ISLANDS.md), [AdaptiveIslandSearchBudgetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchBudgetTests.cs) |
| `AdaptiveVariationPortfolio&lt;TGenome&gt;` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [AdaptiveVariationPortfolioTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveVariationPortfolioTests.cs) |
| `CuriosityEvolutionSelectionPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionSelectionPolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSelectionPolicyTests.cs) |
| `DiagonalCmaEmitter` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [DiagonalCmaEmitterTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DiagonalCmaEmitterTests.cs) |
| `DirectoryEvolutionCheckpointStore` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [DirectoryEvolutionCheckpointStoreTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DirectoryEvolutionCheckpointStoreTests.cs) |
| `DirectoryEvolutionEvaluationStore` | Stable | [PERSISTENT_EVALUATION_REUSE.md](PERSISTENT_EVALUATION_REUSE.md), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `DoubleEvolutionSelectionPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionSelectionPolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSelectionPolicyTests.cs) |
| `DurableEvolutionWorkCoordinator` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [DurableEvolutionWorkCoordinatorTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableEvolutionWorkCoordinatorTests.cs) |
| `EvolutionArchiveEntry&lt;TGenome&gt;` | Stable | [Program.cs](../examples/OperatorCreditSearch/Program.cs), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionArchiveInsertionResult` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `EvolutionArchiveQuery` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionMetricQueryTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMetricQueryTests.cs) |
| `EvolutionArchiveSnapshot&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [ArchiveSnapshotOrderingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ArchiveSnapshotOrderingTests.cs) |
| `EvolutionArtifact` | Stable | [Program.cs](../examples/PolicySearch/Program.cs), [LlmJudgeCritiqueTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmJudgeCritiqueTests.cs) |
| `EvolutionArtifactOptions` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [ArtifactStoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ArtifactStoreParityTests.cs) |
| `EvolutionArtifactSanitizer` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionEvaluationParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionEvaluationParityTests.cs) |
| `EvolutionAskItem&lt;TGenome&gt;` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [EvolutionDurableSessionBridgeTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionDurableSessionBridgeTests.cs) |
| `EvolutionCacheStatus` | Stable | [MEASUREMENT_ORIGIN.md](MEASUREMENT_ORIGIN.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionCandidate&lt;TGenome&gt;` | Stable | [Program.cs](../examples/AdaptiveIslandSearch/Program.cs), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionCanonicalGenome&lt;TGenome&gt;` | Stable | [Program.cs](../examples/AdaptiveIslandSearch/Program.cs), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionCascadeOptions` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionOptionClassificationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionOptionClassificationTests.cs) |
| `EvolutionCellKey` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionCheckpoint` | Stable | [Program.cs](../examples/DurableSession/Program.cs), [DirectoryEvolutionCheckpointStoreTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DirectoryEvolutionCheckpointStoreTests.cs) |
| `EvolutionCheckpointContents&lt;TGenome&gt;` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [EvolutionCheckpointReadingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCheckpointReadingTests.cs) |
| `EvolutionCheckpointDescriptor` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [DirectoryEvolutionCheckpointStoreTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DirectoryEvolutionCheckpointStoreTests.cs) |
| `EvolutionCheckpointEntry&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionCheckpointReadingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCheckpointReadingTests.cs) |
| `EvolutionCheckpointEntrySource` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [EvolutionCheckpointReadingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCheckpointReadingTests.cs) |
| `EvolutionCheckpointRetentionOptions` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [DirectoryEvolutionCheckpointStoreTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DirectoryEvolutionCheckpointStoreTests.cs) |
| `EvolutionCollectionLimits` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [DirectoryEvolutionCheckpointStoreTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DirectoryEvolutionCheckpointStoreTests.cs) |
| `EvolutionCommittedWork` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [DurableEvolutionWorkCoordinatorTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableEvolutionWorkCoordinatorTests.cs) |
| `EvolutionCostedPortfolio` | Stable | [OPERATOR_PORTFOLIOS.md](OPERATOR_PORTFOLIOS.md), [EvolutionOperatorCreditTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionOperatorCreditTests.cs) |
| `EvolutionCostedPortfolioArm&lt;TGenome&gt;` | Stable | [OPERATOR_PORTFOLIOS.md](OPERATOR_PORTFOLIOS.md), [EvolutionOperatorCreditTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionOperatorCreditTests.cs) |
| `EvolutionDescriptorCalibration` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionDescriptorCalibrationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionDescriptorCalibrationTests.cs) |
| `EvolutionDescriptorCalibrationOptions` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionDescriptorCalibrationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionDescriptorCalibrationTests.cs) |
| `EvolutionDescriptorCalibrator` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionDescriptorDefinition` | Stable | [CENTROID_ARCHIVES.md](CENTROID_ARCHIVES.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `EvolutionDiagnostic` | Stable | [ModelChecks.cs](../examples/SurrogateSearch/ModelChecks.cs), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionDispatchMode` | Stable | [PROPOSAL_PIPELINE.md](PROPOSAL_PIPELINE.md), [ProfileTests](../tests/AiDotNet.Evolution.Performance.Tests/ProfileTests.cs) |
| `EvolutionDurableEvaluationPayload` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [EvolutionDurableSessionBridgeTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionDurableSessionBridgeTests.cs) |
| `EvolutionDurableSessionBridge&lt;TGenome&gt;` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [EvolutionDurableSessionBridgeTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionDurableSessionBridgeTests.cs) |
| `EvolutionEarlyStoppingMetric` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [EvolutionEarlyStoppingCriterionTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionEarlyStoppingCriterionTests.cs) |
| `EvolutionEarlyStoppingOptions` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionAuditRegressionTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionAuditRegressionTests.cs) |
| `EvolutionEarlyStoppingOutcome` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [RunPolicyContractTests](../tests/AiDotNet.Evolution.Tests/UnitTests/RunPolicyContractTests.cs) |
| `EvolutionEarlyStoppingReport` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionEarlyStoppingCriterionTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionEarlyStoppingCriterionTests.cs) |
| `EvolutionEarlyStoppingUnmeasurableReason` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionEarlyStoppingCriterionTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionEarlyStoppingCriterionTests.cs) |
| `EvolutionEliteRecord&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionEngine&lt;TGenome&gt;` | Stable | [CENTROID_ARCHIVES.md](CENTROID_ARCHIVES.md), [ProgramVariationPortfolioTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramVariationPortfolioTests.cs) |
| `EvolutionEngineOptions` | Stable | [PERSISTENT_EVALUATION_REUSE.md](PERSISTENT_EVALUATION_REUSE.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `EvolutionEvaluation` | Stable | [MEASUREMENT_ORIGIN.md](MEASUREMENT_ORIGIN.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionEvaluationCacheKey` | Stable | [PERSISTENT_EVALUATION_REUSE.md](PERSISTENT_EVALUATION_REUSE.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `EvolutionEvaluationCacheLookup` | Stable | [Program.cs](../examples/PersistentEvaluation/Program.cs), [EvolutionPersistentEvaluationAccountingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPersistentEvaluationAccountingTests.cs) |
| `EvolutionEvaluationCacheRecord` | Stable | [PERSISTENT_EVALUATION_REUSE.md](PERSISTENT_EVALUATION_REUSE.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `EvolutionEvaluationCacheWriteStatus` | Stable | [Program.cs](../examples/PersistentEvaluation/Program.cs), [EvolutionPersistentEvaluationAccountingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPersistentEvaluationAccountingTests.cs) |
| `EvolutionEvaluationContext` | Stable | [Program.cs](../examples/AdaptiveIslandSearch/Program.cs), [ProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ProgramFitnessEvaluatorTests.cs) |
| `EvolutionEvaluationCost` | Stable | [Program.cs](../examples/OperatorCreditSearch/Program.cs), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionEvaluationReuseDecision` | Stable | [Program.cs](../examples/PersistentEvaluation/Program.cs), [EvolutionPersistentEvaluationAccountingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPersistentEvaluationAccountingTests.cs) |
| `EvolutionEvaluationReuseMode` | Stable | [Program.cs](../examples/PersistentEvaluation/Program.cs), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `EvolutionEvaluationReusePolicy` | Stable | [PERSISTENT_EVALUATION_REUSE.md](PERSISTENT_EVALUATION_REUSE.md), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `EvolutionEvaluationStatus` | Stable | [Program.cs](../examples/OperatorCreditSearch/Program.cs), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionEvent&lt;TGenome&gt;` | Stable | [ReuseCampaign.cs](../examples/PersistentEvaluation/ReuseCampaign.cs), [EvolutionEngineTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionEngineTests.cs) |
| `EvolutionEventKind` | Stable | [ReuseCampaign.cs](../examples/PersistentEvaluation/ReuseCampaign.cs), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionExecutionMode` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionPipelineTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPipelineTests.cs) |
| `EvolutionExploitationSource` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionExternalTaskIdentity` | Stable | [EXTERNAL_WORK_IDENTITY.md](EXTERNAL_WORK_IDENTITY.md), [EvolutionDurableSessionBridgeTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionDurableSessionBridgeTests.cs) |
| `EvolutionFailurePolicy` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [RunPolicyContractTests](../tests/AiDotNet.Evolution.Tests/UnitTests/RunPolicyContractTests.cs) |
| `EvolutionFidelityBatch&lt;TGenome&gt;` | Stable | [MULTI_FIDELITY.md](MULTI_FIDELITY.md), [EvolutionFidelityCheckpointValidationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointValidationTests.cs) |
| `EvolutionFidelityCheckpoint` | Stable | [MULTI_FIDELITY.md](MULTI_FIDELITY.md), [EvolutionFidelityCheckpointTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointTests.cs) |
| `EvolutionFidelityEvaluationContext` | Stable | [IncrementalRegressionTask.cs](../examples/MultiFidelitySearch/IncrementalRegressionTask.cs), [EvolutionFidelityCheckpointTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointTests.cs) |
| `EvolutionFidelityEvaluationResult` | Stable | [IncrementalRegressionTask.cs](../examples/MultiFidelitySearch/IncrementalRegressionTask.cs), [EvolutionFidelityCheckpointTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointTests.cs) |
| `EvolutionFidelityLevel` | Stable | [MULTI_FIDELITY.md](MULTI_FIDELITY.md), [EvolutionFidelityCheckpointTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointTests.cs) |
| `EvolutionFidelityPlan` | Stable | [Program.cs](../examples/MultiFidelitySearch/Program.cs), [EvolutionFidelityCheckpointTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointTests.cs) |
| `EvolutionFidelityPromotion` | Stable | [MULTI_FIDELITY.md](MULTI_FIDELITY.md), [EvolutionFidelityCheckpointTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointTests.cs) |
| `EvolutionFidelityReport&lt;TGenome&gt;` | Stable | [MULTI_FIDELITY.md](MULTI_FIDELITY.md), [EvolutionFidelityCheckpointValidationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointValidationTests.cs) |
| `EvolutionFidelityResumeState` | Stable | [MULTI_FIDELITY.md](MULTI_FIDELITY.md), [EvolutionFidelityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityTests.cs) |
| `EvolutionFidelityScheduler&lt;TGenome&gt;` | Stable | [MULTI_FIDELITY.md](MULTI_FIDELITY.md), [EvolutionFidelityCheckpointTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointTests.cs) |
| `EvolutionFidelityStopReason` | Stable | [RegressionRecovery.cs](../examples/MultiFidelitySearch/RegressionRecovery.cs), [EvolutionFidelityCheckpointTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointTests.cs) |
| `EvolutionGlobalEliteIndex&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionHash` | Stable | [Program.cs](../examples/AdaptiveIslandSearch/Program.cs), [NoveltyAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/NoveltyAdversarialTests.cs) |
| `EvolutionIncumbentChallenge&lt;TGenome&gt;` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionNoisePolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionNoisePolicyTests.cs) |
| `EvolutionIncumbentChallengeReport` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionNoisePolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionNoisePolicyTests.cs) |
| `EvolutionInfeasibleEntry&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `EvolutionIslandAssignmentStrategy` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [AdaptiveIslandSearchTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchTests.cs) |
| `EvolutionIslandDecision` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [AdaptiveIslandSearchStateReviewTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchStateReviewTests.cs) |
| `EvolutionIslandHistory&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionIslandPolicyOptions` | Stable | [ADAPTIVE_ISLANDS.md](ADAPTIVE_ISLANDS.md), [AdaptiveIslandSearchBudgetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchBudgetTests.cs) |
| `EvolutionIslandStatistics` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [AdaptiveIslandSearchBudgetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchBudgetTests.cs) |
| `EvolutionIslandStatus` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionIslandStrategy&lt;TGenome&gt;` | Stable | [ADAPTIVE_ISLANDS.md](ADAPTIVE_ISLANDS.md), [AdaptiveIslandSearchBudgetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchBudgetTests.cs) |
| `EvolutionLineage` | Stable | [Program.cs](../examples/OperatorCreditSearch/Program.cs), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionMeasurementOrigin` | Stable | [MEASUREMENT_ORIGIN.md](MEASUREMENT_ORIGIN.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `EvolutionMeasurementOriginKind` | Stable | [Program.cs](../examples/PersistentEvaluation/Program.cs), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `EvolutionMigration&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionMigrationTopologyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMigrationTopologyTests.cs) |
| `EvolutionMigrationTopology` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionMigrationTopologyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMigrationTopologyTests.cs) |
| `EvolutionMigrationTrigger` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionObjectiveDefinition` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `EvolutionOperatorCostBasis` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [ProgramVariationPortfolioTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramVariationPortfolioTests.cs) |
| `EvolutionOperatorCredit` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [EvolutionOperatorCreditTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionOperatorCreditTests.cs) |
| `EvolutionOperatorRewardKind` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [ProgramVariationPortfolioTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramVariationPortfolioTests.cs) |
| `EvolutionOperatorRewardPolicy` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [ProgramVariationPortfolioTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramVariationPortfolioTests.cs) |
| `EvolutionOperatorStatistics` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [AdaptiveVariationPortfolioTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveVariationPortfolioTests.cs) |
| `EvolutionOptimizationDirection` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionOutOfRangePolicy` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [ArtifactStoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ArtifactStoreParityTests.cs) |
| `EvolutionOutputLayout` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [DirectoryEvolutionCheckpointStoreTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DirectoryEvolutionCheckpointStoreTests.cs) |
| `EvolutionParameter` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [AblationCampaignTests](../tests/AiDotNet.Evolution.Tests/AblationCampaignTests.cs) |
| `EvolutionParameterCondition` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [EvolutionSearchSpaceTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSearchSpaceTests.cs) |
| `EvolutionParameterKind` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [EvolutionNarrowLogDomainTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionNarrowLogDomainTests.cs) |
| `EvolutionParameterValue` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [DiagonalCmaEmitterTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DiagonalCmaEmitterTests.cs) |
| `EvolutionParetoDefinition` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `EvolutionParetoFront&lt;TGenome&gt;` | Stable | [Program.cs](../examples/ParetoSearch/Program.cs), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `EvolutionParetoRepresentative` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `EvolutionPersistentEvaluationCache` | Stable | [PERSISTENT_EVALUATION_REUSE.md](PERSISTENT_EVALUATION_REUSE.md), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `EvolutionPipelineOptions` | Stable | [PROPOSAL_PIPELINE.md](PROPOSAL_PIPELINE.md), [EvolutionOptionClassificationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionOptionClassificationTests.cs) |
| `EvolutionPipelineReport` | Stable | [PROPOSAL_PIPELINE.md](PROPOSAL_PIPELINE.md), [EvolutionPipelineTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPipelineTests.cs) |
| `EvolutionPipelineScheduleEntry` | Stable | [PROPOSAL_PIPELINE.md](PROPOSAL_PIPELINE.md), [EvolutionPipelineTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPipelineTests.cs) |
| `EvolutionPipelineScheduleKind` | Stable | [PROPOSAL_PIPELINE.md](PROPOSAL_PIPELINE.md), [EvolutionPipelineTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPipelineTests.cs) |
| `EvolutionProposalCost` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `EvolutionRefinementContext` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionSearchSpaceTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSearchSpaceTests.cs) |
| `EvolutionRejectionAudit&lt;TGenome&gt;` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionNoisePolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionNoisePolicyTests.cs) |
| `EvolutionRejectionAuditEntry` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionNoisePolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionNoisePolicyTests.cs) |
| `EvolutionRejectionAuditReport` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionNoisePolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionNoisePolicyTests.cs) |
| `EvolutionRepertoire` | Stable | [WARM_START_REPERTOIRES.md](WARM_START_REPERTOIRES.md), [EvolutionPersistentEvaluationAccountingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPersistentEvaluationAccountingTests.cs) |
| `EvolutionRepertoireEntry` | Stable | [WARM_START_REPERTOIRES.md](WARM_START_REPERTOIRES.md), [EvolutionRepertoireTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionRepertoireTests.cs) |
| `EvolutionRepertoireImport&lt;TGenome&gt;` | Stable | [ReuseCampaign.cs](../examples/PersistentEvaluation/ReuseCampaign.cs), [EvolutionRepertoireTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionRepertoireTests.cs) |
| `EvolutionRepertoireImportDecision` | Stable | [WARM_START_REPERTOIRES.md](WARM_START_REPERTOIRES.md), [EvolutionRepertoireTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionRepertoireTests.cs) |
| `EvolutionRepertoireImportStatus` | Stable | [WARM_START_REPERTOIRES.md](WARM_START_REPERTOIRES.md), [EvolutionRepertoireTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionRepertoireTests.cs) |
| `EvolutionRepertoireProvenance` | Stable | [WARM_START_REPERTOIRES.md](WARM_START_REPERTOIRES.md), [EvolutionRepertoireTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionRepertoireTests.cs) |
| `EvolutionReplicateContext` | Stable | [MEASUREMENT_ORIGIN.md](MEASUREMENT_ORIGIN.md), [EvolutionNoisePolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionNoisePolicyTests.cs) |
| `EvolutionReplicateMeasurement` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionFidelityCheckpointValidationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointValidationTests.cs) |
| `EvolutionReplicateRunner&lt;TGenome&gt;` | Stable | [MEASUREMENT_ORIGIN.md](MEASUREMENT_ORIGIN.md), [EvolutionMeasurementOriginTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMeasurementOriginTests.cs) |
| `EvolutionReplicationPlan` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionMeasurementOriginTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMeasurementOriginTests.cs) |
| `EvolutionReplicationPurpose` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionFidelityCheckpointTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointTests.cs) |
| `EvolutionReplicationReport` | Stable | [Program.cs](../examples/ReplicatedEvaluation/Program.cs), [EvolutionFidelityCheckpointValidationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointValidationTests.cs) |
| `EvolutionReplicationStopReason` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionFidelityCheckpointValidationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionFidelityCheckpointValidationTests.cs) |
| `EvolutionResourceAdmission` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [ResourceBudgetWorkflowTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ResourceBudgetWorkflowTests.cs) |
| `EvolutionResourceBoundary` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [ResourceBudgetWorkflowTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ResourceBudgetWorkflowTests.cs) |
| `EvolutionResourceBudgetException` | Stable | [Program.cs](../examples/OperatorCreditSearch/Program.cs), [CSharpProposalSourceTests](../tests/AiDotNet.Evolution.CSharp.Tests/CSharpProposalSourceTests.cs) |
| `EvolutionResourceLedger` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionResourceLimitExceededException` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `EvolutionResourceOutcome` | Stable | [Program.cs](../examples/SurrogateSearch/Program.cs), [CSharpProposalSourceTests](../tests/AiDotNet.Evolution.CSharp.Tests/CSharpProposalSourceTests.cs) |
| `EvolutionResourceReceipt` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [EvolutionReplicationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionReplicationTests.cs) |
| `EvolutionResourceRequest` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [ResourceBudgetWorkflowTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ResourceBudgetWorkflowTests.cs) |
| `EvolutionResourceReservation` | Stable | [Program.cs](../examples/ProposalPipeline/Program.cs), [EvolutionResourceLedgerTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionResourceLedgerTests.cs) |
| `EvolutionResourceResult&lt;T&gt;` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `EvolutionResources` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionResourceSnapshot` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [ResourceStageTotalsTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ResourceStageTotalsTests.cs) |
| `EvolutionResourceStage` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [StandaloneCompilerPortfolioTests](../tests/AiDotNet.Evolution.CSharp.Tests/StandaloneCompilerPortfolioTests.cs) |
| `EvolutionResourceStageSnapshot` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [ResourceStageTotalsTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ResourceStageTotalsTests.cs) |
| `EvolutionResourceWork` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [EvolutionResourceLedgerTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionResourceLedgerTests.cs) |
| `EvolutionRetryStatuses` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionEngineReachabilityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionEngineReachabilityTests.cs) |
| `EvolutionReuseScope` | Stable | [MEASUREMENT_ORIGIN.md](MEASUREMENT_ORIGIN.md), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `EvolutionRunCounters` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionMetricQueryTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMetricQueryTests.cs) |
| `EvolutionRunResult&lt;TGenome&gt;` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [AdaptiveVariationPortfolioTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveVariationPortfolioTests.cs) |
| `EvolutionSearchGenome` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [AllocationBudgetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AllocationBudgetTests.cs) |
| `EvolutionSearchPreset` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionSearchPresetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSearchPresetTests.cs) |
| `EvolutionSearchPresets` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [EvolutionNarrowLogDomainTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionNarrowLogDomainTests.cs) |
| `EvolutionSearchSpace` | Stable | [Program.cs](../examples/OperatorCreditSearch/Program.cs), [AllocationBudgetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AllocationBudgetTests.cs) |
| `EvolutionSearchSpaceBuilder` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [AblationCampaignTests](../tests/AiDotNet.Evolution.Tests/AblationCampaignTests.cs) |
| `EvolutionSearchTask` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [AllocationBudgetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AllocationBudgetTests.cs) |
| `EvolutionSelection&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionSelectionOptions` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `EvolutionSelectionPolicyKind` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [AblationCampaignTests](../tests/AiDotNet.Evolution.Tests/AblationCampaignTests.cs) |
| `EvolutionSession&lt;TGenome&gt;` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [EvolutionDurableSessionBridgeTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionDurableSessionBridgeTests.cs) |
| `EvolutionStopReason` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionContinuousDispatchTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionContinuousDispatchTests.cs) |
| `EvolutionTaskResult` | Stable | [MEASUREMENT_ORIGIN.md](MEASUREMENT_ORIGIN.md), [ProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ProgramFitnessEvaluatorTests.cs) |
| `EvolutionTimingProtocol` | Stable | [REPLICATED_EVALUATION.md](REPLICATED_EVALUATION.md), [EvolutionNoisePolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionNoisePolicyTests.cs) |
| `EvolutionTraceFile` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionMeasurementOriginTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMeasurementOriginTests.cs) |
| `EvolutionTraceFormat` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionMeasurementOriginTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMeasurementOriginTests.cs) |
| `EvolutionTraceObserver&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [CliTests](../tests/AiDotNet.Evolution.Tests/UnitTests/CliTests.cs) |
| `EvolutionTraceOptions` | Stable | [Program.cs](../examples/PersistentEvaluation/Program.cs), [CliTests](../tests/AiDotNet.Evolution.Tests/UnitTests/CliTests.cs) |
| `EvolutionTraceReadResult` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionTraceTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionTraceTests.cs) |
| `EvolutionTraceRecord` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionMeasurementOriginTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMeasurementOriginTests.cs) |
| `EvolutionTraceSummary` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionTraceTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionTraceTests.cs) |
| `EvolutionVariationContext&lt;TGenome&gt;` | Stable | [Program.cs](../examples/AdaptiveIslandSearch/Program.cs), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `EvolutionWorkCommitDisposition` | Stable | [Program.cs](../examples/DurableSession/Program.cs), [DurableEvolutionWorkCoordinatorTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableEvolutionWorkCoordinatorTests.cs) |
| `EvolutionWorkCoordinatorOptions` | Stable | [DURABLE_WORKER_PROTOCOL.md](DURABLE_WORKER_PROTOCOL.md), [DurableEvolutionWorkCoordinatorTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableEvolutionWorkCoordinatorTests.cs) |
| `EvolutionWorkerProfile` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [DurableEvolutionWorkCoordinatorTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableEvolutionWorkCoordinatorTests.cs) |
| `EvolutionWorkHeartbeat` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [DurableEvolutionWorkCoordinatorTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableEvolutionWorkCoordinatorTests.cs) |
| `EvolutionWorkIdentity` | Stable | [EXTERNAL_WORK_IDENTITY.md](EXTERNAL_WORK_IDENTITY.md), [DurableEvolutionWorkCoordinatorTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableEvolutionWorkCoordinatorTests.cs) |
| `EvolutionWorkLease` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [DurableEvolutionWorkCoordinatorTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableEvolutionWorkCoordinatorTests.cs) |
| `EvolutionWorkProtocol` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [DurableConformanceTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableConformanceTests.cs) |
| `EvolutionWorkRequirements` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [DurableEvolutionWorkCoordinatorTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DurableEvolutionWorkCoordinatorTests.cs) |
| `ICandidateRefiner&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionSearchSpaceTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSearchSpaceTests.cs) |
| `ICascadeEvolutionTask&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionMeasurementOriginTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMeasurementOriginTests.cs) |
| `ICheckpointableEvolutionArchive&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionDescriptorGrowthTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionDescriptorGrowthTests.cs) |
| `ICheckpointableParetoArchive&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `ICheckpointableVariationOperator&lt;TGenome&gt;` | Stable | [ADAPTIVE_ISLANDS.md](ADAPTIVE_ISLANDS.md), [AdaptiveIslandSearchStateReviewTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchStateReviewTests.cs) |
| `ICostedEvolutionProposalSource&lt;TGenome&gt;` | Stable | [OFFLINE_POLICY_SEARCH.md](OFFLINE_POLICY_SEARCH.md), [AdaptiveIslandSearchBudgetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchBudgetTests.cs) |
| `IDeterministicConcurrentCostedEvolutionProposalSource&lt;TGenome&gt;` | Stable | [PROPOSAL_PIPELINE.md](PROPOSAL_PIPELINE.md), [EvolutionPipelineTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPipelineTests.cs) |
| `IDeterministicConcurrentVariationOperator&lt;TGenome&gt;` | Stable | [PROPOSAL_PIPELINE.md](PROPOSAL_PIPELINE.md), [EvolutionPipelineTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionPipelineTests.cs) |
| `IEliteIndexAwareEvolutionSelectionPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `IEvolutionArchive&lt;TGenome&gt;` | Stable | [CENTROID_ARCHIVES.md](CENTROID_ARCHIVES.md), [EvolutionEarlyStoppingCriterionTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionEarlyStoppingCriterionTests.cs) |
| `IEvolutionArchiveCellCount` | Stable | [CENTROID_ARCHIVES.md](CENTROID_ARCHIVES.md), [CentroidArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/CentroidArchiveTests.cs) |
| `IEvolutionArchiveView&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [ArchiveSnapshotOrderingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ArchiveSnapshotOrderingTests.cs) |
| `IEvolutionCheckpointStore` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [DiagonalCmaEmitterTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DiagonalCmaEmitterTests.cs) |
| `IEvolutionEvaluationStore` | Stable | [ReuseCampaign.cs](../examples/PersistentEvaluation/ReuseCampaign.cs), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `IEvolutionGenomeCodec&lt;TGenome&gt;` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [EvolutionCheckpointReadingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCheckpointReadingTests.cs) |
| `IEvolutionIslandProposalScheduler` | Stable | [ADAPTIVE_ISLANDS.md](ADAPTIVE_ISLANDS.md), [AdaptiveIslandSearchValidationTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchValidationTests.cs) |
| `IEvolutionObserver&lt;TGenome&gt;` | Stable | [ReuseCampaign.cs](../examples/PersistentEvaluation/ReuseCampaign.cs), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `IEvolutionParetoArchiveView&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `IEvolutionProposalCostProvider` | Stable | [RESOURCE_ACCOUNTING.md](RESOURCE_ACCOUNTING.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `IEvolutionTask&lt;TGenome&gt;` | Stable | [Program.cs](../examples/AdaptiveIslandSearch/Program.cs), [ArtifactStoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ArtifactStoreParityTests.cs) |
| `IGenomeDistance&lt;TGenome&gt;` | Stable | [NOVELTY_MIGRATION.md](migration/NOVELTY_MIGRATION.md), [NoveltyAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/NoveltyAdversarialTests.cs) |
| `IGrowableEvolutionArchive&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionDescriptorGrowthTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionDescriptorGrowthTests.cs) |
| `IImmutableEvolutionGenome&lt;TGenome&gt;` | Stable | [DURABLE_EXTERNAL_WORK.md](DURABLE_EXTERNAL_WORK.md), [EvolutionAuditRegressionTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionAuditRegressionTests.cs) |
| `IInfeasibleExplorationSelectionPolicy&lt;TGenome&gt;` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `IMigrationPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionMigrationTopologyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMigrationTopologyTests.cs) |
| `InMemoryEvolutionCheckpointStore` | Stable | [Program.cs](../examples/AdaptiveIslandSearch/Program.cs), [AdaptiveIslandSearchTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchTests.cs) |
| `IOutcomeAwareEvolutionSelectionPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionSelectionPolicyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSelectionPolicyTests.cs) |
| `IOutcomeAwareVariationOperator&lt;TGenome&gt;` | Stable | [ADAPTIVE_ISLANDS.md](ADAPTIVE_ISLANDS.md), [AdaptiveIslandSearchTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchTests.cs) |
| `ISelectionPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionEngineReachabilityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionEngineReachabilityTests.cs) |
| `IVariationOperator&lt;TGenome&gt;` | Stable | [PROPOSAL_PIPELINE.md](PROPOSAL_PIPELINE.md), [AdaptiveIslandSearchTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AdaptiveIslandSearchTests.cs) |
| `JsonEvolutionCheckpointStore` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [EvolutionCheckpointStoreTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCheckpointStoreTests.cs) |
| `MapElitesArchive&lt;TGenome&gt;` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `ParetoArchive&lt;TGenome&gt;` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `ParetoEvolutionMigrationPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `ParetoEvolutionSelectionPolicy&lt;TGenome&gt;` | Stable | [PARETO_SEARCH.md](PARETO_SEARCH.md), [ParetoArchiveTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ParetoArchiveTests.cs) |
| `RatioEvolutionSelectionPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionCoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionCoreParityTests.cs) |
| `ResourceMeteredEvolutionTask&lt;TGenome&gt;` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [DispatchAutoTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DispatchAutoTests.cs) |
| `ResourceMeteredVariationOperator&lt;TGenome&gt;` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [CSharpProposalSourceTests](../tests/AiDotNet.Evolution.CSharp.Tests/CSharpProposalSourceTests.cs) |
| `RingMigrationPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionMigrationTopologyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMigrationTopologyTests.cs) |
| `SearchSpaceCrossover` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [EvolutionSearchPresetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSearchPresetTests.cs) |
| `SearchSpaceLocalRefiner` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [EvolutionSearchSpaceTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSearchSpaceTests.cs) |
| `SearchSpaceMutation` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [EvolutionSearchPresetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSearchPresetTests.cs) |
| `SearchSpaceRestart` | Stable | [TYPED_SEARCH_SPACES.md](TYPED_SEARCH_SPACES.md), [AllocationBudgetTests](../tests/AiDotNet.Evolution.Tests/UnitTests/AllocationBudgetTests.cs) |
| `StableRandom` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `StableRandomState` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [StableRandomTests](../tests/AiDotNet.Evolution.Tests/UnitTests/StableRandomTests.cs) |
| `TopologyMigrationPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionMigrationTopologyTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionMigrationTopologyTests.cs) |
| `UniformEvolutionSelectionPolicy&lt;TGenome&gt;` | Stable | [SELECTION_ISLANDS_AND_ARCHIVES.md](SELECTION_ISLANDS_AND_ARCHIVES.md), [EvolutionEngineReachabilityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionEngineReachabilityTests.cs) |
| `EvolutionSurrogateObservation&lt;TGenome&gt;` | Experimental (AIDEVO001) | |
| `EvolutionSurrogatePrediction` | Experimental (AIDEVO001) | |
| `EvolutionSurrogateSelection&lt;TGenome&gt;` | Experimental (AIDEVO001) | |
| `EvolutionSurrogateSelectionReason` | Experimental (AIDEVO001) | |
| `EvolutionSurrogateSelector&lt;TGenome&gt;` | Experimental (AIDEVO001) | |
| `EvolutionSurrogateValidationReport` | Experimental (AIDEVO001) | |
| `IEvolutionSurrogateDiagnosticModel` | Experimental (AIDEVO001) | |
| `IEvolutionSurrogateModel&lt;TGenome&gt;` | Experimental (AIDEVO001) | |
| `IEvolutionSurrogateTrainer&lt;TGenome&gt;` | Experimental (AIDEVO001) | |
| `EvolutionPolicyBaseline` | Experimental (AIDEVO002) | |
| `EvolutionPolicyBaselineComparison` | Experimental (AIDEVO002) | |
| `EvolutionPolicyCampaignOutcome` | Experimental (AIDEVO002) | |
| `EvolutionPolicyContext` | Experimental (AIDEVO002) | |
| `EvolutionPolicyEngineOperator&lt;TGenome&gt;` | Experimental (AIDEVO002) | |
| `EvolutionPolicyEngineTrial` | Experimental (AIDEVO002) | |
| `EvolutionPolicyFamilyGain` | Experimental (AIDEVO002) | |
| `EvolutionPolicyObservation` | Experimental (AIDEVO002) | |
| `EvolutionPolicyOptimizationOptions` | Experimental (AIDEVO002) | |
| `EvolutionPolicyOptimizationReport` | Experimental (AIDEVO002) | |
| `EvolutionPolicyOptimizer` | Experimental (AIDEVO002) | |
| `EvolutionPolicySelectionSchedule` | Experimental (AIDEVO002) | |
| `EvolutionPolicySpace` | Experimental (AIDEVO002) | |
| `EvolutionPolicyTrial` | Experimental (AIDEVO002) | |
| `EvolutionPolicyTrialBudget` | Experimental (AIDEVO002) | |
| `EvolutionPolicyTrialPhase` | Experimental (AIDEVO002) | |
| `EvolutionPolicyTrialRecord` | Experimental (AIDEVO002) | |
| `EvolutionSearchPolicy` | Experimental (AIDEVO002) | |
| `CentroidArchive&lt;TGenome&gt;` | Experimental (AIDEVO003) | |
| `CentroidArchiveDefinition` | Experimental (AIDEVO003) | |
| `CentroidArchiveProjection&lt;TGenome&gt;` | Experimental (AIDEVO003) | |
| `EvolutionArchiveProjectionReport` | Experimental (AIDEVO003) | |
| `EscalatingVariationOperator&lt;TGenome&gt;` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [EvolutionRoutingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionRoutingTests.cs) |
| `EvolutionEscalation` | Stable | [OPERATOR_CREDIT.md](OPERATOR_CREDIT.md), [EvolutionRoutingTests](../tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionRoutingTests.cs) |
| `IEvolutionLatencyProfile` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [DispatchAutoTests](../tests/AiDotNet.Evolution.Tests/UnitTests/DispatchAutoTests.cs) |
| `DirectoryEvolutionArtifactStore` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [ArtifactStoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ArtifactStoreParityTests.cs) |
| `IEvolutionArtifactStore` | Stable | [RUNNING_AND_RESUMING.md](RUNNING_AND_RESUMING.md), [ArtifactStoreParityTests](../tests/AiDotNet.Evolution.Tests/UnitTests/ArtifactStoreParityTests.cs) |

## AiDotNet.Evolution.CSharp

| Type | Class | Evidence |
| --- | --- | --- |
| `CompilerChatMessage` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `CompilerChatOptions` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `CompilerChatResponse` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `CompilerChatUsage` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `CSharpProgramCompiler` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [CompilerTests](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTests.cs) |
| `CSharpProgramEvolutionOptions` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [CompilerOptionsMigrationTests](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerOptionsMigrationTests.cs) |
| `CSharpProgramSourceOptions` | Stable | [PROGRAM_CONSUMER_MIGRATION.md](migration/PROGRAM_CONSUMER_MIGRATION.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `CSharpProgramVariation` | Stable | [PROGRAM_CONSUMER_MIGRATION.md](migration/PROGRAM_CONSUMER_MIGRATION.md), [StandaloneCompilerPortfolioTests](../tests/AiDotNet.Evolution.CSharp.Tests/StandaloneCompilerPortfolioTests.cs) |
| `ICSharpProposalClient` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |

## AiDotNet.Evolution.Deployment

| Type | Class | Evidence |
| --- | --- | --- |
| `AiDotNet.Evolution.AutoML.MapElitesAutoMLArchiveEntry` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [MapElitesAutoMLIntegrationTests](../tests/AiDotNet.Evolution.Deployment.Tests/MapElitesAutoMLIntegrationTests.cs) |
| `AiDotNet.Evolution.AutoML.MapElitesAutoMLOptions` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `EvolutionDeployableArtifact` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `EvolutionDeploymentArtifactKind` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `EvolutionDeploymentArtifactRegistry` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentLifecycleTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentLifecycleTests.cs) |
| `EvolutionDeploymentDecision` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `EvolutionDeploymentEnvelope` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentLifecycleTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentLifecycleTests.cs) |
| `EvolutionDeploymentEvaluators` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `EvolutionDeploymentLifecycle` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [DeploymentOwnershipTests](../tests/AiDotNet.Evolution.Deployment.Tests/DeploymentOwnershipTests.cs) |
| `EvolutionDeploymentMeasurement` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `EvolutionDeploymentPolicy` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `EvolutionDeploymentRetuneRequest` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `EvolutionDeploymentRetuners` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `EvolutionDeploymentSelection` | Stable | [DEPLOYMENT.md](DEPLOYMENT.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |
| `ProgramDeploymentSearchOptions` | Stable | [DEPLOYMENT_MIGRATION.md](migration/DEPLOYMENT_MIGRATION.md), [EvolutionDeploymentAdapterTests](../tests/AiDotNet.Evolution.Deployment.Tests/EvolutionDeploymentAdapterTests.cs) |

## AiDotNet.Evolution.Programs

| Type | Class | Evidence |
| --- | --- | --- |
| `CompilationDiagnostic` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [EvolveBlockExtractionTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/EvolveBlockExtractionTests.cs) |
| `CompilationDiagnosticSeverity` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [EvolveBlockExtractionTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/EvolveBlockExtractionTests.cs) |
| `DelegateProgramFitnessEvaluator` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ProgramFitnessEvaluatorTests.cs) |
| `DirectoryProgramSampleEvidenceStore` | Stable | [NOISY_REUSE_CAMPAIGN.md](NOISY_REUSE_CAMPAIGN.md), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `EditTarget` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `EvolveBlock` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [EvolveBlockExtractionTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/EvolveBlockExtractionTests.cs) |
| `EvolveBlockExtractionResult` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [EvolveBlockExtractionTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/EvolveBlockExtractionTests.cs) |
| `EvolveBlockMarkers` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramDiffTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramDiffTests.cs) |
| `EvolveBlockRegion` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [EvolveBlockExtractionTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/EvolveBlockExtractionTests.cs) |
| `EvolveBlockStatus` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [EvolveBlockExtractionTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/EvolveBlockExtractionTests.cs) |
| `FencedCodeBlock` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [FencedCodeExtractorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/FencedCodeExtractorTests.cs) |
| `FencedCodeExtractionResult` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [FencedCodeExtractorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/FencedCodeExtractorTests.cs) |
| `FencedCodeExtractor` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [FencedCodeExtractorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/FencedCodeExtractorTests.cs) |
| `FencedCodeSelectionSource` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [FencedCodeExtractorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/FencedCodeExtractorTests.cs) |
| `ICostedProgramProposalSource` | Stable | [PROGRAM_CONSUMER_MIGRATION.md](migration/PROGRAM_CONSUMER_MIGRATION.md), [ProgramVariationPortfolioTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramVariationPortfolioTests.cs) |
| `ImprovementOptions` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `ImprovementResult` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [PythonImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/PythonImprovementTests.cs) |
| `InputOutputProgramFitnessEvaluator` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `IProgramChatClient` | Stable | [JUDGE_RUNTIME_MIGRATION.md](migration/JUDGE_RUNTIME_MIGRATION.md), [FakeChatClient](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/FakeChatClient.cs) |
| `IProgramChatClientDecorator` | Stable | [JUDGE_RUNTIME_MIGRATION.md](migration/JUDGE_RUNTIME_MIGRATION.md), [JudgeAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/JudgeAdversarialTests.cs) |
| `IProgramCompiler` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `IProgramDescriptor` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramDescriptorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramDescriptorTests.cs) |
| `IProgramExecutionEngine` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `IProgramExecutionTelemetrySource` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `IProgramFitnessEvaluator` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `IProgramMeasurementEvidenceStore` | Stable | [PROGRAM_SAMPLE_EVIDENCE.md](PROGRAM_SAMPLE_EVIDENCE.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `IProgramResourceLedgerProvider` | Stable | [PROGRAM_CONSUMER_MIGRATION.md](migration/PROGRAM_CONSUMER_MIGRATION.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `IProgramVariationOperator` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `IProposalProvenanceSink` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProposalProvenanceTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProposalProvenanceTests.cs) |
| `IRebasableProgramDescriptor` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `IVersionedProgramDescriptor` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `LlmFeedbackOptions` | Stable | [JUDGE_RUNTIME_MIGRATION.md](migration/JUDGE_RUNTIME_MIGRATION.md), [JudgeAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/JudgeAdversarialTests.cs) |
| `LlmJudgeProgramFitnessEvaluator` | Stable | [JUDGE_RUNTIME_MIGRATION.md](migration/JUDGE_RUNTIME_MIGRATION.md), [JudgeAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/JudgeAdversarialTests.cs) |
| `LlmProgramVariationOperator` | Stable | [MODEL_RUNTIME_MIGRATION.md](migration/MODEL_RUNTIME_MIGRATION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `LlmProgramVariationOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `MeteredProgramVariationOperator` | Stable | [PROGRAM_CONSUMER_MIGRATION.md](migration/PROGRAM_CONSUMER_MIGRATION.md), [ProgramVariationPortfolioTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramVariationPortfolioTests.cs) |
| `Metrics.ProgramMetricAggregationResult` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramMetricAggregatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ScriptMetrics/ProgramMetricAggregatorTests.cs) |
| `Metrics.ProgramMetricAggregator` | Stable | [SCRIPT_METRICS_MIGRATION.md](migration/SCRIPT_METRICS_MIGRATION.md), [ProgramMetricAggregatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ScriptMetrics/ProgramMetricAggregatorTests.cs) |
| `Metrics.ProgramMetricIssue` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramMetricAggregatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ScriptMetrics/ProgramMetricAggregatorTests.cs) |
| `Metrics.ProgramMetricValue` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [EvolveBlockExtractionTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/EvolveBlockExtractionTests.cs) |
| `Novelty.EmbeddingBatch` | Stable | [NOVELTY_MIGRATION.md](migration/NOVELTY_MIGRATION.md), [DeterministicEmbeddingClient](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/DeterministicEmbeddingClient.cs) |
| `Novelty.EmbeddingCosineGenomeDistance` | Stable | [NOVELTY_MIGRATION.md](migration/NOVELTY_MIGRATION.md), [NoveltyAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/NoveltyAdversarialTests.cs) |
| `Novelty.EmbeddingNoveltyOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [NoveltyGatingProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/NoveltyGatingProgramFitnessEvaluatorTests.cs) |
| `Novelty.EmbeddingVector` | Stable | [NOVELTY_MIGRATION.md](migration/NOVELTY_MIGRATION.md), [DeterministicEmbeddingClient](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/DeterministicEmbeddingClient.cs) |
| `Novelty.IProgramEmbeddingClient` | Stable | [NOVELTY_MIGRATION.md](migration/NOVELTY_MIGRATION.md), [DeterministicEmbeddingClient](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/DeterministicEmbeddingClient.cs) |
| `Novelty.IProgramNoveltyJudge` | Stable | [NOVELTY_MIGRATION.md](migration/NOVELTY_MIGRATION.md), [NoveltyTestDoubles](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/NoveltyTestDoubles.cs) |
| `Novelty.LlmProgramNoveltyJudge` | Stable | [NOVELTY_MIGRATION.md](migration/NOVELTY_MIGRATION.md), [LlmProgramNoveltyJudgeTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/LlmProgramNoveltyJudgeTests.cs) |
| `Novelty.NoveltyGatingProgramFitnessEvaluator` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExperienceAndAdvisoryNoveltyTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/ExperienceAndAdvisoryNoveltyTests.cs) |
| `Novelty.ProgramLineEditDistance` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [NoveltyAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/NoveltyAdversarialTests.cs) |
| `Novelty.ProgramNoveltyDecision` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [NoveltyGatingProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/NoveltyGatingProgramFitnessEvaluatorTests.cs) |
| `Novelty.ProgramNoveltyPolicy` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExperienceAndAdvisoryNoveltyTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/ExperienceAndAdvisoryNoveltyTests.cs) |
| `Novelty.ProgramNoveltyStage` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [NoveltyGatingProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/NoveltyGatingProgramFitnessEvaluatorTests.cs) |
| `Novelty.ProgramNoveltyVerdict` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExperienceAndAdvisoryNoveltyTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/ExperienceAndAdvisoryNoveltyTests.cs) |
| `Novelty.ProgramTokenSetDistance` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [NoveltyGatingProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/NoveltyGatingProgramFitnessEvaluatorTests.cs) |
| `PatchPlan` | Stable | [Program.cs](../examples/CompilerGuidedSearch/Program.cs), [CompilerTests](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTests.cs) |
| `PersistentProgramFitnessEvaluator` | Stable | [PROGRAM_SAMPLE_EVIDENCE.md](PROGRAM_SAMPLE_EVIDENCE.md), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `ProcessProgramExecutionEngine` | Stable | [EXECUTION_RUNTIME_MIGRATION.md](migration/EXECUTION_RUNTIME_MIGRATION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `ProgramArtifact` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `ProgramBuild` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `ProgramChatMessage` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [FakeChatClient](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/FakeChatClient.cs) |
| `ProgramChatOptions` | Stable | [JUDGE_RUNTIME_MIGRATION.md](migration/JUDGE_RUNTIME_MIGRATION.md), [FakeChatClient](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/FakeChatClient.cs) |
| `ProgramChatResponse` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [FakeChatClient](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/FakeChatClient.cs) |
| `ProgramChatResponseFormat` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmJudgeProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmJudgeProgramFitnessEvaluatorTests.cs) |
| `ProgramChatRole` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptBuilderTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptBuilderTests.cs) |
| `ProgramChatUsage` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [FakeChatClient](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/FakeChatClient.cs) |
| `ProgramDescriptorSet` | Stable | [PROGRAM_FOUNDATION_MIGRATION.md](migration/PROGRAM_FOUNDATION_MIGRATION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramDiff` | Stable | [PROGRAM_FOUNDATION_MIGRATION.md](migration/PROGRAM_FOUNDATION_MIGRATION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramDiffApplyResult` | Stable | [PROGRAM_FOUNDATION_MIGRATION.md](migration/PROGRAM_FOUNDATION_MIGRATION.md), [ProgramDiffTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramDiffTests.cs) |
| `ProgramDiffBlock` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramDiffFailure` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramDiffTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramDiffTests.cs) |
| `ProgramDiffFailureReason` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramDiffOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ModelRuntimeAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ModelRuntimeAdversarialTests.cs) |
| `ProgramDiffParseResult` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramDiffTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramDiffTests.cs) |
| `ProgramDiffTargetSplit` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramDiversityDescriptor` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramEvolution` | Stable | [PROGRAM_CONSUMER_MIGRATION.md](migration/PROGRAM_CONSUMER_MIGRATION.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `ProgramEvolutionLlmUsage` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `ProgramEvolutionMode` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `ProgramEvolutionPromptOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `ProgramEvolutionResourceOptions` | Stable | [PROGRAM_CONSUMER_MIGRATION.md](migration/PROGRAM_CONSUMER_MIGRATION.md), [PersistentProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/PersistentProgramFitnessEvaluatorTests.cs) |
| `ProgramEvolutionTask` | Stable | [EXECUTION_RUNTIME_MIGRATION.md](migration/EXECUTION_RUNTIME_MIGRATION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramExecuteErrorCode` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `ProgramExecuteRequest` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `ProgramExecuteResponse` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `ProgramGenome` | Stable | [NOVELTY_MIGRATION.md](migration/NOVELTY_MIGRATION.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `ProgramGenomeCodec` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `ProgramImprovement` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `ProgramInputOutputExample` | Stable | [EXECUTION_RUNTIME_MIGRATION.md](migration/EXECUTION_RUNTIME_MIGRATION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `ProgramInterpreterSpecification` | Stable | [EXECUTION_RUNTIME_MIGRATION.md](migration/EXECUTION_RUNTIME_MIGRATION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `ProgramJudgeMember` | Stable | [JUDGE_RUNTIME_MIGRATION.md](migration/JUDGE_RUNTIME_MIGRATION.md), [JudgeAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/JudgeAdversarialTests.cs) |
| `ProgramJudgePanel` | Stable | [JUDGE_RUNTIME_MIGRATION.md](migration/JUDGE_RUNTIME_MIGRATION.md), [JudgeAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/JudgeAdversarialTests.cs) |
| `ProgramLanguage` | Stable | [EXECUTION_RUNTIME_MIGRATION.md](migration/EXECUTION_RUNTIME_MIGRATION.md), [CompilerTestSupport](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs) |
| `ProgramLanguageDetector` | Stable | [PROGRAM_FOUNDATION_MIGRATION.md](migration/PROGRAM_FOUNDATION_MIGRATION.md), [ProgramLanguageDetectorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramLanguageDetectorTests.cs) |
| `ProgramLengthDescriptor` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramMeasurementObservation` | Stable | [PROGRAM_SAMPLE_EVIDENCE.md](PROGRAM_SAMPLE_EVIDENCE.md), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `ProgramMeasurementStatistics` | Stable | [PROGRAM_SAMPLE_EVIDENCE.md](PROGRAM_SAMPLE_EVIDENCE.md), [DirectoryProgramSampleEvidenceStoreTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/DirectoryProgramSampleEvidenceStoreTests.cs) |
| `ProgramMetricAggregationOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramMetricAggregatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ScriptMetrics/ProgramMetricAggregatorTests.cs) |
| `ProgramMetricAggregationStrategy` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramMetricAggregatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ScriptMetrics/ProgramMetricAggregatorTests.cs) |
| `ProgramMetricIssueReason` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramMetricAggregatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ScriptMetrics/ProgramMetricAggregatorTests.cs) |
| `ProgramMetricValueKind` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [EvolveBlockExtractionTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/EvolveBlockExtractionTests.cs) |
| `ProgramNoiseEvaluationOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramNoiseEvaluationTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramNoiseEvaluationTests.cs) |
| `ProgramNoiseEvaluationSession` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramNoiseEvaluationTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramNoiseEvaluationTests.cs) |
| `ProgramNoiseScreenEntry` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramNoiseEvaluationTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramNoiseEvaluationTests.cs) |
| `ProgramNoiseScreenReport` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramNoiseEvaluationTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramNoiseEvaluationTests.cs) |
| `ProgramOutputComparison` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFitnessEvaluatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ProgramFitnessEvaluatorTests.cs) |
| `ProgramPlanner` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `ProgramPromptEvolutionMode` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `ProgramPromptExampleKind` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptBuilderTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptBuilderTests.cs) |
| `ProgramPromptFragmentKey` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptTemplateTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptTemplateTests.cs) |
| `ProgramPromptSystemMessageMode` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ModelRuntimeAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ModelRuntimeAdversarialTests.cs) |
| `ProgramPromptTemplateKey` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `ProgramProposalAttempt` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `ProgramProposalOptions` | Stable | [MODEL_RUNTIME_MIGRATION.md](migration/MODEL_RUNTIME_MIGRATION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `ProgramProposalOutcome` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmProgramVariationOperatorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmProgramVariationOperatorTests.cs) |
| `ProgramSandboxLimitOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramSandboxOptionsTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ProgramSandboxOptionsTests.cs) |
| `ProgramSandboxMode` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProcessProgramExecutionEngineTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ProcessProgramExecutionEngineTests.cs) |
| `ProgramSandboxOptions` | Stable | [EXECUTION_RUNTIME_MIGRATION.md](migration/EXECUTION_RUNTIME_MIGRATION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `ProgramSnapshot` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [CompilerTests](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTests.cs) |
| `ProgramTaskOptions` | Stable | [PROGRAM_FOUNDATION_MIGRATION.md](migration/PROGRAM_FOUNDATION_MIGRATION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramTokenComplexityDescriptor` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramFoundationAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramFoundationAdversarialTests.cs) |
| `ProgramVariationPortfolio` | Stable | [PROGRAM_CONSUMER_MIGRATION.md](migration/PROGRAM_CONSUMER_MIGRATION.md), [ProgramVariationPortfolioTests](../tests/AiDotNet.Evolution.CSharp.Tests/ProgramTypes/ProgramVariationPortfolioTests.cs) |
| `ProgramVerifier` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `ProposalProvenanceOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProposalProvenanceTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProposalProvenanceTests.cs) |
| `Provenance.InMemoryProposalProvenanceSink` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProposalProvenanceTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProposalProvenanceTests.cs) |
| `Provenance.JsonLinesProposalProvenanceSink` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProposalProvenanceTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProposalProvenanceTests.cs) |
| `Provenance.ProposalProvenanceLineage` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProposalProvenanceTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProposalProvenanceTests.cs) |
| `Provenance.ProposalProvenanceLineageStep` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProposalProvenanceTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProposalProvenanceTests.cs) |
| `Provenance.ProposalProvenanceReader` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProposalProvenanceTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProposalProvenanceTests.cs) |
| `Provenance.ProposalProvenanceReadResult` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProposalProvenanceTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProposalProvenanceTests.cs) |
| `Provenance.ProposalProvenanceRecord` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProposalProvenanceTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProposalProvenanceTests.cs) |
| `SandboxedProgramFitnessEvaluator` | Stable | [EXECUTION_RUNTIME_MIGRATION.md](migration/EXECUTION_RUNTIME_MIGRATION.md), [ExecutionAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/Execution/ExecutionAdversarialTests.cs) |
| `ScriptProgramEvaluationOptions` | Stable | [SCRIPT_METRICS_MIGRATION.md](migration/SCRIPT_METRICS_MIGRATION.md), [ScriptEvidenceAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ScriptMetrics/ScriptEvidenceAdversarialTests.cs) |
| `ScriptProgramFitnessEvaluator` | Stable | [SCRIPT_METRICS_MIGRATION.md](migration/SCRIPT_METRICS_MIGRATION.md), [ScriptEvidenceAdversarialTests](../tests/AiDotNet.Evolution.CSharp.Tests/ScriptMetrics/ScriptEvidenceAdversarialTests.cs) |
| `SearchRequest` | Stable | [COMPILER_GUIDED_PROGRAMS.md](COMPILER_GUIDED_PROGRAMS.md), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `SourceEdit` | Stable | [Program.cs](../examples/CompilerGuidedSearch/Program.cs), [CompilerTests](../tests/AiDotNet.Evolution.CSharp.Tests/CompilerTests.cs) |
| `VerificationReceipt` | Stable | [Program.cs](../examples/CompilerGuidedSearch/Program.cs), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `VerificationRequest` | Stable | [Program.cs](../examples/CompilerGuidedSearch/Program.cs), [ImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/ImprovementTests.cs) |
| `AiDotNet.Evolution.Prompts.ProgramPromptArtifact` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptBuilderTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptBuilderTests.cs) |
| `AiDotNet.Evolution.Prompts.ProgramPromptAttempt` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptBuilderTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptBuilderTests.cs) |
| `AiDotNet.Evolution.Prompts.ProgramPromptBuilder` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [LlmJudgeCritiqueTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/LlmJudgeCritiqueTests.cs) |
| `AiDotNet.Evolution.Prompts.ProgramPromptContext` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptBuilderTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptBuilderTests.cs) |
| `AiDotNet.Evolution.Prompts.ProgramPromptExample` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptBuilderTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptBuilderTests.cs) |
| `AiDotNet.Evolution.Prompts.ProgramPromptResult` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptBuilderTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptBuilderTests.cs) |
| `AiDotNet.Evolution.Prompts.ProgramPromptTemplate` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptTemplateTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptTemplateTests.cs) |
| `AiDotNet.Evolution.Prompts.ProgramPromptTemplateSet` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ProgramPromptBuilderTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/ProgramPromptBuilderTests.cs) |
| `AiDotNet.Evolution.Prompts.PromptTextRedactor` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [PromptTextRedactorTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/PromptTextRedactorTests.cs) |
| `Experience.ProgramEvidencePartition` | Experimental (AIDEVO004) | |
| `Experience.ProgramExperienceOutcome` | Experimental (AIDEVO004) | |
| `Experience.ProgramExperienceQuery` | Experimental (AIDEVO004) | |
| `Experience.ProgramExperienceRecord` | Experimental (AIDEVO004) | |
| `Experience.ProgramExperienceRetrieval` | Experimental (AIDEVO004) | |
| `Experience.ProgramExperienceStore` | Experimental (AIDEVO004) | |
| `Novelty.ProgramNoveltyEnforcement` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ExperienceAndAdvisoryNoveltyTests](../tests/AiDotNet.Evolution.CSharp.Tests/Novelty/ExperienceAndAdvisoryNoveltyTests.cs) |
| `PythonProgramCompiler` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [PythonImprovementTests](../tests/AiDotNet.Evolution.CSharp.Tests/PythonImprovementTests.cs) |
| `ClaudeCodeChatClient` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ClaudeCodeChatClientTests](../tests/AiDotNet.Evolution.CSharp.Tests/Providers/ClaudeCodeChatClientTests.cs) |
| `ClaudeCodeChatClientOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ClaudeCodeChatClientTests](../tests/AiDotNet.Evolution.CSharp.Tests/Providers/ClaudeCodeChatClientTests.cs) |
| `ManualProgramChatClient` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ChatProviderTests](../tests/AiDotNet.Evolution.CSharp.Tests/Providers/ChatProviderTests.cs) |
| `OpenAiCompatibleChatClient` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ChatProviderTests](../tests/AiDotNet.Evolution.CSharp.Tests/Providers/ChatProviderTests.cs) |
| `OpenAiCompatibleChatClientOptions` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ChatProviderTests](../tests/AiDotNet.Evolution.CSharp.Tests/Providers/ChatProviderTests.cs) |
| `ProgramReasoningEffort` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [ChatProviderTests](../tests/AiDotNet.Evolution.CSharp.Tests/Providers/ChatProviderTests.cs) |
| `WeightedChatModel` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [WeightedEnsembleChatClientTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/WeightedEnsembleChatClientTests.cs) |
| `WeightedEnsembleChatClient` | Stable | [PROGRAM_EVOLUTION.md](PROGRAM_EVOLUTION.md), [WeightedEnsembleChatClientTests](../tests/AiDotNet.Evolution.CSharp.Tests/ModelRuntime/WeightedEnsembleChatClientTests.cs) |

## AiDotNet.Evolution.Surrogates

| Type | Class | Evidence |
| --- | --- | --- |
| `NumericSurrogateOptions` | Experimental (AIDEVO001) | |
| `NumericSurrogateValidation` | Experimental (AIDEVO001) | |
| `ValidatedNearestNeighborModel` | Experimental (AIDEVO001) | |
| `ValidatedNearestNeighborTrainer` | Experimental (AIDEVO001) | |
