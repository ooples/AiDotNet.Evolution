/**
 * Publishes the NativeAOT host for one runtime identifier into `bin/<rid>/`.
 *
 * Used by CI to fill the per-platform packages, and by anyone running the tests from a
 * clone. NativeAOT cross-compilation is not supported, so each RID is built on its own
 * machine -- the script deliberately refuses a RID it cannot produce here rather than
 * emitting a binary for the wrong platform.
 */

import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const packageRoot = resolve(here, '..');
const repoRoot = resolve(packageRoot, '..', '..');

const NATIVE_RID = {
  'win32-x64': 'win-x64',
  'linux-x64': 'linux-x64',
  'darwin-arm64': 'osx-arm64',
};

const host = `${process.platform}-${process.arch}`;
const rid = process.argv[2] ?? NATIVE_RID[host];

if (!rid) {
  console.error(`No published runtime identifier for ${host}.`);
  process.exit(1);
}
if (rid !== NATIVE_RID[host]) {
  console.error(
    `Refusing to build '${rid}' on ${host}: NativeAOT does not cross-compile, ` +
      `so this would either fail or produce a binary for the wrong platform.`
  );
  process.exit(1);
}

const project = join(repoRoot, 'src', 'AiDotNet.Evolution.Host');
// NO -p:PublishAot=true HERE, even though that is the reflex. A -p property flows into
// every referenced project, and the library multi-targets net471, for which AOT is not a
// thing -- so passing it fails the restore with NETSDK1207 before anything is compiled.
// The host's own csproj sets PublishAot, which is the only place it belongs.
const publish = spawnSync('dotnet', ['publish', project, '-c', 'Release', '-r', rid], {
  stdio: 'inherit',
  shell: false,
});
if (publish.status !== 0) process.exit(publish.status ?? 1);

const executable = process.platform === 'win32' ? 'aidotnet-evolution-host.exe' : 'aidotnet-evolution-host';
const built = join(project, 'bin', 'Release', 'net8.0', rid, 'native', executable);
if (!existsSync(built)) {
  console.error(`dotnet publish reported success but '${built}' is not there.`);
  process.exit(1);
}

const destination = join(packageRoot, 'bin', rid);
mkdirSync(destination, { recursive: true });
copyFileSync(built, join(destination, executable));
console.log(`host -> ${join(destination, executable)}`);
