import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { inspectContent, inspectPath, normalizePath } from './repository-policy.mjs';

const ignoreFile = new URL('../../.gitignore', import.meta.url);

function checkIgnored(paths) {
  const root = mkdtempSync(join(tmpdir(), 'astra-policy-'));
  execFileSync('git', ['init', '--quiet'], { cwd: root });
  writeFileSync(join(root, '.gitignore'), readFileSync(ignoreFile, 'utf8'));
  try {
    return execFileSync('git', ['check-ignore', '--no-index', '--stdin'], {
      cwd: root, encoding: 'utf8', input: `${paths.join('\n')}\n`,
    }).trim().split(/\r?\n/).filter(Boolean);
  } catch (error) {
    return String(error.stdout ?? '').trim().split(/\r?\n/).filter(Boolean);
  }
}

test('gitignore rejects generated, credential, log, and assistant paths at any depth', () => {
  const forbidden = ['node_modules/pkg/index.js', 'client/dist/app.js', 'src/bin/a.dll', 'src/obj/a.o', 'App_Data/db.sqlite', 'nested/App_Data/cache', 'logs/app.log', 'nested/logs/error.txt', '.claude/settings.json', 'nested/.codex/config.toml', '.clodex/state', '.ai/cache', '.env', 'nested/.env.local', 'server/appsettings.json', 'server/appsettings.Development.json', 'keys/service.pem', 'debug.log'];
  assert.deepEqual(checkIgnored(forbidden).sort(), forbidden.sort());
});

test('gitignore allows metadata, source, lockfiles, and examples', () => {
  const allowed = ['.gitignore', 'README.md', 'src/index.js', 'client/package-lock.json', 'appsettings.example.json', 'server/appsettings.example.json', '.env.example'];
  assert.deepEqual(checkIgnored(allowed), []);
});

test('path parser normalizes separators and reports safe rule names', () => {
  assert.equal(normalizePath('.\\nested\\node_modules\\x.js'), 'nested/node_modules/x.js');
  assert.deepEqual(inspectPath('server/appsettings.example.json'), []);
  assert.deepEqual(inspectPath('nested/.codex/config.toml'), [{ path: 'nested/.codex/config.toml', rule: 'AI assistant state' }]);
});

test('secret parser recognizes only high-confidence forms', () => {
  assert.equal(inspectContent('config.txt', `token=${'ghp_' + 'a'.repeat(36)}`)[0].rule, 'GitHub token');
  assert.equal(inspectContent('config.txt', `id=${'AKIA' + 'A'.repeat(16)}`)[0].rule, 'AWS access key');
  assert.equal(inspectContent('config.txt', 'AKIA-short and ghp_short').length, 0);
  assert.deepEqual(inspectContent('.github/scripts/repository-policy.mjs', `token=${'ghp_' + 'a'.repeat(36)}`), []);
});
