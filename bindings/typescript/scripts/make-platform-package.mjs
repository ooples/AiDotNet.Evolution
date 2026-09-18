/**
 * Turns `bin/<rid>/` into a publishable per-platform package under `packages/<rid>/`.
 *
 * The main package declares all three as OPTIONAL dependencies with `os`/`cpu` fields, so
 * npm installs only the one that matches and silently skips the rest. That is the reason
 * for splitting at all: a single package carrying three NativeAOT binaries would make
 * every install download about 18MB to use 6MB of it.
 */

import { copyFileSync, mkdirSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const packageRoot = resolve(here, '..');

const PLATFORMS = {
  'win-x64': { os: 'win32', cpu: 'x64', executable: 'aidotnet-evolution-host.exe' },
  'linux-x64': { os: 'linux', cpu: 'x64', executable: 'aidotnet-evolution-host' },
  'osx-arm64': { os: 'darwin', cpu: 'arm64', executable: 'aidotnet-evolution-host' },
};

const rid = process.argv[2];
const platform = PLATFORMS[rid];
if (!platform) {
  console.error(`Usage: node make-platform-package.mjs <${Object.keys(PLATFORMS).join('|')}>`);
  process.exit(1);
}

const source = join(packageRoot, 'bin', rid, platform.executable);
if (!existsSync(source)) {
  console.error(`No binary at '${source}'. Run 'npm run host' on a ${rid} machine first.`);
  process.exit(1);
}

// The platform packages must carry the SAME version as the package that depends on them,
// so it is read from there rather than repeated here.
const { version } = JSON.parse(readFileSync(join(packageRoot, 'package.json'), 'utf8'));

const destination = join(packageRoot, 'packages', rid);
mkdirSync(destination, { recursive: true });
copyFileSync(source, join(destination, platform.executable));

writeFileSync(
  join(destination, 'package.json'),
  `${JSON.stringify(
    {
      name: `@aidotnet/evolution-host-${rid}`,
      version,
      description: `The AiDotNet.Evolution NativeAOT host for ${rid}.`,
      license: 'Apache-2.0',
      repository: {
        type: 'git',
        url: 'git+https://github.com/ooples/AiDotNet.Evolution.git',
        directory: 'bindings/typescript',
      },
      os: [platform.os],
      cpu: [platform.cpu],
      files: [platform.executable],
    },
    null,
    2
  )}\n`
);

console.log(`packaged -> ${destination}`);
