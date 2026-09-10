#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const FORBIDDEN_PATH_RULES = [
  ['dependencies', /(^|\/)node_modules\//i],
  ['build output', /(^|\/)(?:dist|bin|obj)\//i],
  ['runtime data', /(^|\/)App_Data\//i],
  ['logs', /(^|\/)logs?\//i],
  ['AI assistant state', /(^|\/)\.(?:claude|codex|clodex|ai|cursor|continue)(?:\/|$)/i],
  ['environment file', /(^|\/)\.env(?:\.[^/]*)?$/i],
  ['local appsettings', /(^|\/)appsettings(?:\.[^/]*)?\.json$/i],
  ['private key file', /\.(?:pem|key)$/i],
];

const SECRET_RULES = [
  ['private key', /-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----/],
  ['GitHub token', /\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,255}\b/],
  ['GitHub fine-grained token', /\bgithub_pat_[A-Za-z0-9_]{82,255}\b/],
  ['AWS access key', /\b(?:AKIA|ASIA)[A-Z0-9]{16}\b/],
];

const ALLOWED_BASENAMES = new Set(['appsettings.example.json', '.env.example']);
const CONTENT_SCAN_EXCLUSIONS = new Set(['.github/scripts/repository-policy.mjs', '.github/scripts/repository-policy.test.mjs']);

export function normalizePath(path) {
  return path.replaceAll('\\', '/').replace(/^\.\//, '');
}

export function inspectPath(path) {
  const normalized = normalizePath(path);
  if (ALLOWED_BASENAMES.has(normalized.split('/').at(-1).toLowerCase())) return [];
  return FORBIDDEN_PATH_RULES.filter(([, pattern]) => pattern.test(normalized))
    .map(([rule]) => ({ path: normalized, rule }));
}

export function inspectContent(path, content) {
  const normalized = normalizePath(path);
  if (CONTENT_SCAN_EXCLUSIONS.has(normalized)) return [];
  return SECRET_RULES.filter(([, pattern]) => pattern.test(content))
    .map(([rule]) => ({ path: normalized, rule }));
}

function git(args, options = {}) {
  return execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], ...options });
}

function nulPaths(output) {
  return output.split('\0').filter(Boolean).map(normalizePath);
}

export function repositoryViolations(root = process.cwd()) {
  const tracked = nulPaths(git(['ls-files', '-z'], { cwd: root }));
  const staged = nulPaths(git(['diff', '--cached', '--name-only', '--diff-filter=ACMR', '-z'], { cwd: root }));
  const paths = [...new Set([...tracked, ...staged])];
  const stagedSet = new Set(staged);
  const violations = paths.flatMap(inspectPath);
  for (const path of paths) {
    let content;
    try {
      content = stagedSet.has(path)
        ? git(['show', `:${path}`], { cwd: root, maxBuffer: 2 * 1024 * 1024 })
        : readFileSync(`${root}/${path}`, 'utf8');
    } catch {
      continue;
    }
    if (content.includes('\0')) continue;
    violations.push(...inspectContent(path, content));
  }
  return violations;
}

export function formatViolation({ path, rule }) {
  return `${path}: ${rule}`;
}

const invokedDirectly = process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1];
if (invokedDirectly) {
  try {
    const violations = repositoryViolations();
    if (violations.length) {
      console.error('Repository policy violations:');
      for (const violation of violations) console.error(`- ${formatViolation(violation)}`);
      process.exitCode = 1;
    } else {
      console.log('Repository policy passed.');
    }
  } catch (error) {
    console.error(`Repository policy could not run: ${error.message}`);
    process.exitCode = 2;
  }
}
