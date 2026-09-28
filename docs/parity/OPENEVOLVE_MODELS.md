# OpenEvolve parity: model providers and options (V1-51, #164)

Every per-model option of OpenEvolve 0.3.2 (`LLMModelConfig`, commit 411fb59), with its equivalent here and the test
that proves it. Library users get the same clients directly; CLI users set them in the run file's `model` section.

| OpenEvolve option | AiDotNet.Evolution equivalent | Proven by |
| --- | --- | --- |
| `provider: openai`, `api_base`, `api_key` | `OpenAiCompatibleChatClient` (`Endpoint`, `ApiKey`); CLI `model.provider`, `endpoint`, `apiKeyEnvironmentVariable` | `OpenAiCompatibleChatClientTests` |
| `provider: claude_code` | `ClaudeCodeChatClient`; CLI `"provider": "ClaudeCode"` | `ClaudeCodeChatClientTests` |
| `init_client` (custom client) | any `IProgramChatClient` passed to `LlmProgramVariationOperator` | existing operator tests |
| `manual_mode` (human in the loop) | `ManualProgramChatClient`; CLI `"provider": "Manual"` | `ManualProgramChatClientTests`, `CliRunTests` |
| `name` | `Model` / CLI `model.name` | `OpenAiCompatibleChatClientTests` |
| `temperature` | `LlmProgramVariationOptions.Temperature` | `OpenAiCompatibleChatClientTests` |
| `top_p` | `LlmProgramVariationOptions.TopP` | `OpenAiCompatibleChatClientTests` |
| `max_tokens` | `LlmProgramVariationOptions.MaxOutputTokens` | `OpenAiCompatibleChatClientTests` |
| `random_seed` | `LlmProgramVariationOptions.Seed` (otherwise derived from the proposal's seeded stream) | `OpenAiCompatibleChatClientTests` |
| `reasoning_effort` | `LlmProgramVariationOptions.ReasoningEffort` (`ProgramReasoningEffort`) | `OpenAiCompatibleChatClientTests` |
| `system_message` | `LlmProgramVariationOptions.SystemMessage` | existing operator tests |
| `timeout` | `Timeout` on each client; CLI `model.timeoutSeconds` | `CliRunTests` |
| `retries` | `MaxRetries` on each client; CLI `model.maxRetries` | `OpenAiCompatibleChatClientTests`, `ClaudeCodeChatClientTests` |
| `retry_delay` | `RetryDelay` (doubling) on each client; CLI `model.retryDelaySeconds` | `OpenAiCompatibleChatClientTests` |
| `max_budget_usd` | `ClaudeCodeChatClientOptions.MaxBudgetUsd`; CLI `model.maxBudgetUsd` | `ClaudeCodeChatClientTests` |

## Differences worth knowing
- OpenEvolve's `claude_code` provider passes the prompt as a command-line argument, keeps only the user turns, and runs
  with the user's profile. `ClaudeCodeChatClient` sends every turn on standard input, reads token usage from the CLI's
  JSON result, and by default runs isolated (no tools, MCP servers, setting sources or inherited `ANTHROPIC_` variables).
- Only throttling, server errors, timeouts and transport failures are retried; a 4xx request error fails at once.
  The retry delay doubles without random jitter, so a replay makes the same calls in the same order.
- A model-call timeout that is not the run's own cancellation surfaces as `TimeoutException`, so it counts as a failed
  call (and a run where every call fails exits with code 3).