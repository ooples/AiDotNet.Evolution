/**
 * Finding the NativeAOT host binary.
 *
 * Three sources, tried in this order, because each covers a case the others cannot:
 * an environment variable for someone pointing at a build of their own, a directory
 * inside this package for the published artifact, and an optional per-platform
 * dependency for an install that pulled only its own platform's binary.
 */

import { existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

/** Set this to a path to use a host you built yourself. */
export const HOST_PATH_ENV = 'AIDOTNET_EVOLUTION_HOST';

/** The .NET runtime identifiers this package publishes a binary for. */
export const SUPPORTED_PLATFORMS = ['win-x64', 'linux-x64', 'osx-arm64'] as const;

export type SupportedPlatform = (typeof SUPPORTED_PLATFORMS)[number];

/**
 * The .NET runtime identifier for the machine we are on, or null when there is none.
 *
 * Null rather than a throw: the caller can still supply `hostPath`, and a package that
 * refuses to even LOAD on an unpublished platform is useless to someone who compiled the
 * host themselves.
 */
export function currentPlatform(): SupportedPlatform | null {
  const key = `${process.platform}-${process.arch}`;
  switch (key) {
    case 'win32-x64':
      return 'win-x64';
    case 'linux-x64':
      return 'linux-x64';
    case 'darwin-arm64':
      return 'osx-arm64';
    default:
      return null;
  }
}

const EXECUTABLE = process.platform === 'win32' ? 'aidotnet-evolution-host.exe' : 'aidotnet-evolution-host';

const packageRoot = (): string => {
  // Two levels up from dist/binary.js is the package root; the same is true of src/ under
  // ts-node, which is why this is computed rather than hardcoded to one of them.
  const here = dirname(fileURLToPath(import.meta.url));
  return join(here, '..');
};

/**
 * The path to the host binary.
 *
 * @throws {Error} When no binary can be found, with all three places it looked.
 */
export function resolveHostBinary(): string {
  const override = process.env[HOST_PATH_ENV];
  if (override) {
    if (!existsSync(override)) {
      throw new Error(`${HOST_PATH_ENV} points at '${override}', which does not exist.`);
    }
    return override;
  }

  const platform = currentPlatform();
  const tried: string[] = [];

  if (platform) {
    const bundled = join(packageRoot(), 'bin', platform, EXECUTABLE);
    if (existsSync(bundled)) return bundled;
    tried.push(bundled);

    // The per-platform package, resolved rather than imported: it is an OPTIONAL
    // dependency, so on any other platform npm skipped it and require would throw.
    const dependency = `@aidotnet/evolution-host-${platform}`;
    try {
      const require_ = createRequire(import.meta.url);
      const entry = require_.resolve(`${dependency}/${EXECUTABLE}`);
      if (existsSync(entry)) return entry;
      tried.push(entry);
    } catch {
      tried.push(`${dependency} (not installed)`);
    }
  }

  const platformNote = platform
    ? `for ${platform}`
    : `for ${process.platform}-${process.arch}, which this package does not publish a binary for ` +
      `(published: ${SUPPORTED_PLATFORMS.join(', ')})`;

  throw new Error(
    `Could not find the AiDotNet.Evolution host binary ${platformNote}.\n` +
      (tried.length > 0 ? `Looked in:\n${tried.map((path) => `  - ${path}`).join('\n')}\n` : '') +
      `Build it with 'dotnet publish src/AiDotNet.Evolution.Host -c Release -r <rid>' and set ` +
      `${HOST_PATH_ENV} to the result, or pass 'hostPath' when opening a session.`
  );
}
