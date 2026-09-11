# @aidotnet/evolution

Quality-diversity evolutionary search for TypeScript, running the
[AiDotNet.Evolution](https://github.com/ooples/AiDotNet.Evolution) engine.

No .NET installation required: the package ships a NativeAOT binary for the supported
platforms -- **win-x64**, **linux-x64** and **osx-arm64**. Anywhere else (osx-x64,
linux-arm64, and the rest) there is no published binary, and you build the host
yourself -- see [Building the host yourself](#building-the-host-yourself).

```bash
npm install @aidotnet/evolution
```

## Use it

You provide the fitness function; the library provides the search.

```ts
import { evolve } from '@aidotnet/evolution';

const summary = await evolve(
  {
    parameters: [
      { name: 'learningRate', min: 0.0001, max: 0.1, step: 0.0001 },
      { name: 'layers', min: 1, max: 12, integral: true },
    ],
    // Behaviour dimensions, not objectives. The archive keeps the best candidate found in
    // each REGION of this space, so you finish with a set of genuinely different good
    // answers rather than one.
    descriptors: [
      { name: 'layers', min: 1, max: 12, bins: 12 },
      { name: 'trainingSeconds', min: 0, max: 600, bins: 10 },
    ],
    maxProposals: 500,
    maxEvaluations: 500,
    batchSize: 8,
  },
  async (candidates) => {
    // One call per BATCH. Evaluate them however you like -- in parallel, on other
    // machines, by asking a human -- and report when you have the answers.
    const scored = await Promise.all(candidates.map(runTrainingJob));
    return scored.map((result, index) => ({
      evaluationId: candidates[index].evaluationId,
      quality: result.accuracy,
      descriptors: { layers: result.layers, trainingSeconds: result.seconds },
    }));
  }
);

console.log(summary.best);
```

### Driving the loop yourself

`evolve` is a wrapper over ask/tell. Use the session directly when evolution has to be
interleaved with other work -- a queue, a UI, a long-running service.

```ts
import { openSession } from '@aidotnet/evolution';

const session = await openSession({ parameters, descriptors });
try {
  for (;;) {
    const batch = await session.ask(8);
    if (batch.length === 0) break; // an empty batch is the completion signal
    await session.tell(batch.map(score));
  }
} finally {
  const { best, stopReason } = await session.close();
}
```

`close()` is where the result comes from, because it stops the run **gracefully**: the
current batch commits and the archive is kept. It is idempotent, but can throw if the host
cannot confirm shutdown. To preserve an earlier evaluator error, catch a cleanup failure
and rethrow that original error, as `evolve()` does.

A pending `ask()` does not prevent `tell()` or `close()` from being processed. Closing
cancels pending asks with an error, not an empty successful batch. The native protocol
permits up to 32 waiting asks and returns a correlated error when that bound is exceeded.

## Strict external-work identity

For new external integrations, enable strict mode and return the whole issued ticket:

```ts
const session = await openSession({
  parameters, descriptors,
  taskIdentity: {
    taskId: 'training-search',
    taskVersionHash: 'task-data-and-canonicalization-v1',
    evaluatorVersionHash: 'training-protocol-v1',
  },
  evaluationTimeoutMs: 60_000,
  maxRetries: 1,
});
const batch = await session.ask(8);
await session.tell(batch.map(candidate => ({
  evaluationId: candidate.evaluationId,
  workIdentity: candidate.workIdentity,
  quality: score(candidate.parameters),
  descriptors: describe(candidate.parameters),
})));
await session.close();
```

The example supplies domain-specific `parameters`, `descriptors`, `score` and `describe`.
For a complete search, place ask/tell inside the loop above, or use `evolve` with the same
ticket-preserving result mapping. Handle close failures as shown above.

Strict mode verifies the host's fencing capability at open; an old host cannot silently
ignore the new fields. `session.compatibilityHash` pins the task, evaluator, parameter codec,
archive and engine contract. Version fingerprints are the caller's responsibility.

A retry reuses the evaluation ID but gets a different ticket. Never substitute the newest
ticket for an old worker's result. Stale and duplicate results return zero acceptance;
missing/malformed tickets are rejected. Timeout does not kill remote work or refund its cost.
Configurations without `taskIdentity` preserve the legacy numeric-ID mode; retries require
strict mode. Neither mode currently persists host sessions across process restarts.

## Reporting a failure

A candidate that could not be evaluated is not a candidate that scored zero.

```ts
return [{ evaluationId: candidate.evaluationId, reason: 'the build did not compile' }];
```

Omit `quality` (or pass a non-finite number) and the evaluation is recorded as failed. A
zero would enter the archive as a genuinely poor result and quietly compete; a failure
competes with nothing, and a run where everything failed returns `best: null` rather than
a plausible-looking answer.

## API

| | |
| --- | --- |
| `evolve(config, evaluate)` | Runs the whole search. Returns `{ best, stopReason }`. |
| `openSession(config)` | Starts a run you drive yourself. |
| `session.ask(max)` | The next candidates. An empty array means the run is over. |
| `session.tell(results)` | Reports scores. Returns how many were outstanding. |
| `session.close()` | Stops gracefully and returns the best genome. Idempotent. |
| `resolveHostBinary()` | The path to the host binary this package would spawn. |

### `RunConfig`

| field | meaning |
| --- | --- |
| `parameters` | The knobs to vary. `step` defaults to a hundredth of the range. |
| `descriptors` | The behaviour space the archive is organised by. |
| `seeds` | Starting points. Defaults to the midpoint of every range. |
| `seed` | The RNG seed. The same seed replays the same search. |
| `maxProposals` | How many candidates may be proposed. |
| `maxEvaluations` | How many may be *evaluated*. A separate cap; defaults to `maxProposals`. |
| `maxGenerations`, `batchSize` | |
| `direction` | `'maximize'` (default) or `'minimize'`. Anything else is rejected. |
| `hostPath`, `hostArgs` | Run a host you built yourself. |

Parameter bounds must be finite, ordered (`min < max`), and have a finite `max-min`.
The resolved step must be finite and positive, and `(max-min)/step` must remain finite.
Integral parameters require at least one integer between `ceil(min)` and `floor(max)`;
their quantization grid starts at `ceil(min)`. Unsupported numeric domains are rejected
when opening the session rather than silently collapsing the search.

## Why a child process rather than a native binding

The engine's surface is ask/tell, which is coarse-grained: one call per *batch* of
candidates, not per evaluation. At that granularity a process hop costs almost nothing,
and paying it removes an ABI to keep in sync with the C# side, a C++ toolchain in CI, a
prebuild matrix over Node ABI versions, and the possibility that a fault in native code
takes the Node process down with it.

This works at all because the host never calls back. Evolution normally drives the loop
and calls *your* evaluator; here you drive it. With callbacks, the child would have to
reach back into the Node event loop and the transport would have to be an FFI binding.

## Building the host yourself

Needed only when working on a platform the package does not publish for, or on the engine
itself.

```bash
npm run host                              # publishes for this machine into bin/<rid>/
AIDOTNET_EVOLUTION_HOST=/path/to/host npm test
```

NativeAOT does not cross-compile, so each platform's binary is built on that platform.
Published: `win-x64`, `linux-x64`, `osx-arm64`.

## Maintainer: first-publication lockfile transition

The three optional native packages must exist on npm before recording their resolved
versions in the binding's lockfile. Until then, CI deliberately uses `npm install` and
the bootstrap lockfile remains ignored; this is not a fully reproducible install.

After publishing the three packages at the versions in `optionalDependencies`:

1. Verify each version with `npm view <package>@<version> version`.
2. From a clean binding checkout, run `npm install --package-lock-only` and confirm the
   lockfile contains resolved URLs and integrity hashes for all three native packages.
3. Remove the `package-lock.json` ignore entry, commit the generated lockfile, and change
   the dependency-install step in `.github/workflows/typescript-binding.yml` to `npm ci`.
4. Run the workflow's full platform/Node matrix; each run must execute its real-host
   engine tests, and `scripts/check-test-output.mjs` must report no missing or skipped tests.

## Licence

Apache-2.0.
