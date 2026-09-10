#!/usr/bin/env node
// CI 커버리지 요약 — 외부 서비스 없이 GitHub Step Summary에 마크다운 표를 출력한다.
// 서버: coverlet cobertura XML(test-results/**/coverage.cobertura.xml)
// 클라이언트: vitest v8 json-summary(client/coverage/coverage-summary.json)
// 어느 쪽이 없어도 실패하지 않고 "리포트 없음"으로 표기한다(부분 도입 단계 허용).
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

const SERVER_RESULTS_DIR = process.argv[2] ?? 'test-results';
const CLIENT_SUMMARY_PATH = process.argv[3] ?? join('client', 'coverage', 'coverage-summary.json');

function findFiles(dir, name, found = []) {
  let entries;
  try { entries = readdirSync(dir); } catch { return found; }
  for (const entry of entries) {
    const path = join(dir, entry);
    let info;
    try { info = statSync(path); } catch { continue; }
    if (info.isDirectory()) findFiles(path, name, found);
    else if (entry === name) found.push({ path, mtime: info.mtimeMs });
  }
  return found;
}

const pct = (rate) => `${(rate * 100).toFixed(1)}%`;

function serverRows() {
  const candidates = findFiles(SERVER_RESULTS_DIR, 'coverage.cobertura.xml')
    .sort((a, b) => b.mtime - a.mtime);
  if (candidates.length === 0) return [['server (.NET)', '리포트 없음', '리포트 없음']];
  const xml = readFileSync(candidates[0].path, 'utf8');
  const attr = (tag, name) => {
    const match = new RegExp(`${name}="([^"]*)"`).exec(tag);
    return match ? match[1] : null;
  };
  const coverageTag = /<coverage\b[^>]*>/.exec(xml)?.[0] ?? '';
  const rows = [[
    'server (.NET) 전체',
    `${pct(Number(attr(coverageTag, 'line-rate')))} (${attr(coverageTag, 'lines-covered')}/${attr(coverageTag, 'lines-valid')})`,
    pct(Number(attr(coverageTag, 'branch-rate'))),
  ]];
  for (const packageTag of xml.match(/<package\b[^>]*>/g) ?? []) {
    rows.push([
      `· ${attr(packageTag, 'name')}`,
      pct(Number(attr(packageTag, 'line-rate'))),
      pct(Number(attr(packageTag, 'branch-rate'))),
    ]);
  }
  return rows;
}

function clientRows() {
  let total;
  try { total = JSON.parse(readFileSync(CLIENT_SUMMARY_PATH, 'utf8')).total; }
  catch { return [['client (React)', '리포트 없음', '리포트 없음']]; }
  return [[
    'client (React) 전체',
    `${total.lines.pct.toFixed(1)}% (${total.lines.covered}/${total.lines.total})`,
    `${total.branches.pct.toFixed(1)}%`,
  ]];
}

const rows = [...serverRows(), ...clientRows()];
const lines = [
  '## 커버리지 요약 (이슈 #6 캠페인)',
  '',
  '| 대상 | 라인 | 브랜치 |',
  '| --- | --- | --- |',
  ...rows.map((row) => `| ${row.join(' | ')} |`),
  '',
];
console.log(lines.join('\n'));
