import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const json = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const environmentKeys = ['BenchmarkDotNetVersion', 'OsVersion', 'ProcessorName', 'PhysicalCoreCount',
  'LogicalCoreCount', 'RuntimeVersion', 'Architecture', 'DotNetCliVersion', 'Configuration'];

export function compareRuns(baseline, candidate) {
  if (baseline.metadata.Schema !== 'performance-run/v1') throw new Error('Unsupported report schema');
  for (const key of ['Schema', 'Workload', 'WorkloadSha256', 'HarnessSha256'])
    if (!baseline.metadata[key] || baseline.metadata[key] !== candidate.metadata[key]) throw new Error(`Incompatible ${key}`);
  for (const key of environmentKeys)
    if (baseline.environment[key] === undefined || candidate.environment[key] === undefined || baseline.environment[key] !== candidate.environment[key]) throw new Error(`Different environment: ${key}`);
  if (baseline.rows.length !== candidate.rows.length) throw new Error('Different benchmark case counts');
  const candidates = new Map(candidate.rows.map(row => [row.FullName, row]));
  if (new Set(baseline.rows.map(row => row.FullName)).size !== baseline.rows.length) throw new Error('Duplicate baseline cases');
  if (candidates.size !== candidate.rows.length) throw new Error('Duplicate benchmark cases');
  return baseline.rows.map(before => {
    const after = candidates.get(before.FullName);
    if (!after) throw new Error(`Missing case: ${before.FullName}`);
    for (const row of [before, after])
      if (!(row.Statistics?.Mean > 0) || !Number.isFinite(row.Statistics.Mean) || !Number.isFinite(row.Statistics.StandardDeviation)
          || !(row.Memory?.BytesAllocatedPerOperation >= 0) || !Number.isFinite(row.Memory.BytesAllocatedPerOperation))
        throw new Error(`Incomplete measurements: ${row.FullName}`);
    const job = row => row.DisplayInfo?.match(/\((IterationCount=[^)]+)\)/)?.[1];
    if (job(before) !== job(after)) throw new Error(`Different measurement job: ${before.FullName}`);
    return { name: before.FullName, baselineMs: before.Statistics.Mean / 1e6, candidateMs: after.Statistics.Mean / 1e6,
      meanChangePercent: (after.Statistics.Mean / before.Statistics.Mean - 1) * 100,
      allocationChangePercent: before.Memory.BytesAllocatedPerOperation === 0 ? null
        : (after.Memory.BytesAllocatedPerOperation / before.Memory.BytesAllocatedPerOperation - 1) * 100,
      baselineCvPercent: before.Statistics.StandardDeviation / before.Statistics.Mean * 100,
      candidateCvPercent: after.Statistics.StandardDeviation / after.Statistics.Mean * 100 };
  });
}

function load(directory) {
  const files = fs.readdirSync(path.join(directory, 'results')).filter(file => file.endsWith('-full-compressed.json'));
  if (files.length === 0) throw new Error('No reports found');
  const reports = files.map(file => json(path.join(directory, 'results', file)));
  for (const report of reports)
    for (const key of environmentKeys)
      if (report.HostEnvironmentInfo[key] !== reports[0].HostEnvironmentInfo[key]) throw new Error(`Mixed report environments: ${key}`);
  return { metadata: json(path.join(directory, 'run-metadata.json')), environment: reports[0].HostEnvironmentInfo,
    rows: reports.flatMap(report => report.Benchmarks) };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 4) throw new Error('Usage: node scripts/compare-benchmarks.mjs <baseline-directory> <candidate-directory>');
  const comparisons = compareRuns(load(process.argv[2]), load(process.argv[3]));
  process.stdout.write('| Case | Baseline ms | Candidate ms | Mean change | Allocated change | CV before/after |\n|---|---:|---:|---:|---:|---:|\n');
  for (const row of comparisons) process.stdout.write(`| ${row.name} | ${row.baselineMs.toFixed(2)} | ${row.candidateMs.toFixed(2)} | ${row.meanChangePercent.toFixed(1)}% | ${row.allocationChangePercent?.toFixed(1) ?? 'n/a'}% | ${row.baselineCvPercent.toFixed(1)}% / ${row.candidateCvPercent.toFixed(1)}% |\n`);
  process.stdout.write('\nInformational comparison; timing noise and confidence intervals require review. No timing threshold is enforced.\n');
}
