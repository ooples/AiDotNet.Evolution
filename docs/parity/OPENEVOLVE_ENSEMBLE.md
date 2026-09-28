# OpenEvolve and AlphaEvolve parity: weighted model ensembles (V1-50, #163)

| Source | Option | AiDotNet.Evolution equivalent | Proven by |
| --- | --- | --- | --- |
| OpenEvolve | `llm.models` (list with `weight`) | `WeightedEnsembleChatClient` over `WeightedChatModel(client, weight)` | `WeightedEnsembleChatClientTests` |
| OpenEvolve | `primary_model` / `primary_model_weight`, `secondary_model` / `secondary_model_weight` | a two-member `WeightedEnsembleChatClient` | `WeightedEnsembleChatClientTests` |
| OpenEvolve | `llm.evaluator_models` | give the LLM judge its own `WeightedEnsembleChatClient` | existing judge tests |
| AlphaEvolve | fast model for breadth, strong model for depth | a two-member ensemble weighted toward the fast model | `WeightedEnsembleChatClientTests` |

## How it differs from OpenEvolve
- **Replayable.** The member is chosen from the proposal's seed (derived from the run's seeded stream), so a replay sends
  every proposal to the same model. OpenEvolve draws from `random` state that depends on scheduling.
- **Recorded.** Each response carries the member that answered, and `GetMemberStatistics` reports calls and failures per
  member.
- **No silent re-routing.** A failing member is charged the failure and the exception propagates; the call is never
  quietly sent to another model.
- **Composable.** The ensemble is a chat client, so it works under the escalation ladder and the adaptive portfolio,
  which choose among operators one level up.