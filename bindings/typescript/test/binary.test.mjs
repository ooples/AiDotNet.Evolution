import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { HOST_PATH_ENV, SUPPORTED_PLATFORMS, currentPlatform, resolveHostBinary } from '../dist/index.js';

const withEnv = (value, body) => {
  const previous = process.env[HOST_PATH_ENV];
  if (value === null) delete process.env[HOST_PATH_ENV];
  else process.env[HOST_PATH_ENV] = value;
  try {
    return body();
  } finally {
    if (previous === undefined) delete process.env[HOST_PATH_ENV];
    else process.env[HOST_PATH_ENV] = previous;
  }
};

test('the override wins when it points at a real file', () => {
  const directory = mkdtempSync(join(tmpdir(), 'evolution-host-'));
  const fake = join(directory, 'host');
  writeFileSync(fake, '');
  assert.equal(withEnv(fake, resolveHostBinary), fake);
});

test('an override pointing nowhere is an error naming the variable', () => {
  const missing = join(tmpdir(), 'definitely-not-here-9f3a2b');
  assert.throws(() => withEnv(missing, resolveHostBinary), (error) => {
    assert.match(error.message, new RegExp(HOST_PATH_ENV));
    assert.ok(error.message.includes(missing), 'the message must name the bad path');
    return true;
  });
});

test('with no override, resolution either finds a binary or says how to build one', () => {
  // Whether a binary is present depends on whether `npm run host` has been run here, so
  // the assertion is on the CONTRACT rather than on one of the two outcomes: a returned
  // path must exist, and a failure must be actionable rather than a bare "not found".
  withEnv(null, () => {
    let resolved = null;
    try {
      resolved = resolveHostBinary();
    } catch (error) {
      assert.match(error.message, /dotnet publish/, 'the error must say how to get a binary');
      assert.match(error.message, new RegExp(HOST_PATH_ENV), 'and how to point at one');
      return;
    }
    assert.ok(existsSync(resolved), `resolved '${resolved}', which is not there`);
  });
});

test('currentPlatform maps this machine to a published runtime identifier or null', () => {
  const platform = currentPlatform();
  if (platform !== null) {
    assert.ok(SUPPORTED_PLATFORMS.includes(platform), `${platform} is not in the published set`);
  } else {
    assert.ok(
      !['win32-x64', 'linux-x64', 'darwin-arm64'].includes(`${process.platform}-${process.arch}`),
      'a platform we publish for must not map to null'
    );
  }
});

test('the published set is exactly the three platforms CI builds', () => {
  assert.deepEqual([...SUPPORTED_PLATFORMS], ['win-x64', 'linux-x64', 'osx-arm64']);
});
