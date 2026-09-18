# AiDotNet.Evolution.Deployment

Optional program/trained-model deployment with exact applicability, independent
paired validation, quarantine/rollback and explicitly drained bounded retuning.
Deployment and MAP-Elites orchestration are implemented here; the aliased AiDotNet
dependency supplies generic model primitives only.

Create a registry, six-hash `EvolutionDeploymentEnvelope`, an explicit policy,
a known-valid fallback and an independent evaluator. Stage artifacts without
activating them, promote through `EvolutionDeploymentLifecycle`, and select only
at controlled batch boundaries. Keep hidden validation outside private searches.

`EvolutionDeploymentRetuners.Program` takes `ProgramDeploymentSearchOptions`.
`EvolutionDeploymentRetuners.AutoML` takes relocated `AiDotNet.Evolution.AutoML`
options; trained weights, not just hyperparameters, are retained and reloaded.
This is not an execution/deserialization sandbox or an automatic background tuner.
Source license is BSL, retained in `AIDOTNET-LICENSE.txt`.

During the AiDotNet split, explicitly alias the model package to prevent old
embedded Evolution names from entering the consumer's global namespace:

```xml
<PackageReference Include="AiDotNet.Evolution.Deployment" Version="0.1.0-preview.1" />
<PackageReference Include="AiDotNet" Version="0.231.0" Aliases="AiDotNetConsumer" />
```

Use `extern alias AiDotNetConsumer;` for model primitives. The executable fixture
under `eng/fixtures/DeploymentConsumer` demonstrates package-only consumption.
