# Weighted model ensembles

Use `EvolutionWeightedModelEnsemble<TGenome>` when more than one model should write proposals in
one run and you want to control how often each is asked. Each entry is an
`EvolutionModelWeight<TGenome>` pairing a proposal source with a non-negative weight; a model is
drawn with probability `weight / sum(weights)` on the run's own random stream, so the same seed
replays the same sequence of models.

```csharp
var ensemble = new EvolutionWeightedModelEnsemble<MyGenome>("ensemble-v1", new[]
{
    new EvolutionModelWeight<MyGenome>(fastModel, 3.0),   // asked three times as often
    new EvolutionModelWeight<MyGenome>(strongModel, 1.0)
});

var ledger = new EvolutionResourceLedger("run", EvolutionResources.Of("cost_units", 1000));
var metered = new ResourceMeteredVariationOperator<MyGenome>(
    ensemble, ledger, EvolutionResources.Of("cost_units", 20), "cost-units-v1");
// Supply `metered` to the engine, or as one arm of an AdaptiveVariationPortfolio.
```

## What it does and does not decide

The ensemble decides **which model writes a proposal**. `EscalatingVariationOperator<TGenome>` and
`AdaptiveVariationPortfolio<TGenome>` decide **which operator is asked at all**, and they take
operators rather than proposal sources. So the stack is always ensemble inside
`ResourceMeteredVariationOperator<TGenome>` inside a ladder tier or a portfolio arm — never the
reverse. A portfolio that shifts its arm weights changes how often the ensemble is asked, not which
model it then draws.

## Outcomes are routed, not broadcast

`Observe` goes only to the model that produced that proposal. A child that learns from outcomes — a
bandit arm, an escalation ladder — must not be taught another model's result, so an outcome for a
generation the ensemble did not route is refused rather than ignored, and a generation already
awaiting an outcome cannot be proposed again.

## Attribution

`Accounts` reports one `EvolutionModelAccount` per model: its `ModelId`, how many `Proposals` it
wrote and how many `Failures` it was charged. A model is charged a failure when it throws or when
it returns a `Failed` outcome without throwing; a cancellation is the run stopping rather than the
model failing, so it is not charged and releases its route.

`TryGetDraw(generation, out draw)` answers which model wrote one generation's proposal, as an
`EvolutionModelDraw` carrying the model's id and its position in the configured order. The cost
receipt is identified by the metered operator's operation id, which names the ensemble rather than
the model it drew, so this is what attributes that charge. It is available from the draw until the
outcome is observed — which is when a trace record for that evaluation is written — and it survives
a checkpoint, so a resumed run can still charge the right model.

`CaptureState` and `RestoreState` carry the per-model counters, the unrouted proposals and the
children's own state, so a resumed run keeps both the attribution and the routing. A state whose
pending table is absent, or that carries more unrouted proposals than the ensemble will route, is
refused: an omitted table is not the same claim as an empty one.
