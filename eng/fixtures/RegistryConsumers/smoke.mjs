// Fresh-consumer check for the npm packages (E-5): the installed @aidotnet/evolution must be the expected version,
// resolve its platform host package, and run a real search through it.
import { readFileSync } from 'node:fs';
import { evolve } from '@aidotnet/evolution';

const expected = process.argv[2];
// The package's exports map does not expose package.json, so the installed manifest is read from disk.
const { version } = JSON.parse(readFileSync(new URL('./node_modules/@aidotnet/evolution/package.json', import.meta.url), 'utf8'));
if (version !== expected) throw new Error(`installed @aidotnet/evolution ${version}, expected ${expected}`);

const summary = await evolve(
  {
    parameters: [{ name: 'x', min: 0, max: 1 }],
    descriptors: [{ name: 'x', min: 0, max: 1, bins: 4 }],
    maxProposals: 32,
    maxEvaluations: 32,
    batchSize: 8,
  },
  (candidates) => candidates.map((candidate) => ({
    evaluationId: candidate.evaluationId,
    quality: -((candidate.parameters.x - 0.3) ** 2),
    descriptors: { x: candidate.parameters.x },
  }))
);
if (summary.best === null) throw new Error('the search returned no best candidate');
console.log(`@aidotnet/evolution ${version}: best x=${summary.best.parameters.x.toFixed(4)}, stop=${summary.stopReason}`);