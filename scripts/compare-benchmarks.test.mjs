import assert from 'node:assert/strict';
import test from 'node:test';
import { compareRuns } from './compare-benchmarks.mjs';
const run = () => ({ metadata: { Schema: 'performance-run/v1', Workload: 'v1', WorkloadSha256: 'data', HarnessSha256: 'harness' },
  environment: { BenchmarkDotNetVersion: 'test', OsVersion: 'test', ProcessorName: 'test', PhysicalCoreCount: 4, LogicalCoreCount: 8, RuntimeVersion: 'test', Architecture: 'X64', DotNetCliVersion: 'test', Configuration: 'RELEASE' }, rows: [{ FullName: 'A', Statistics: { Mean: 100, StandardDeviation: 10 }, Memory: { BytesAllocatedPerOperation: 200 } }] });
test('compares identical work and reports changes and noise', () => {
  const before = run(), after = run(); after.rows[0].Statistics.Mean = 150;
  after.rows[0].Memory.BytesAllocatedPerOperation = 100;
  const result = compareRuns(before, after)[0];
  assert.equal(result.meanChangePercent, 50); assert.equal(result.allocationChangePercent, -50); assert.equal(result.baselineCvPercent, 10);
});
test('rejects mismatched workloads and environments', () => {
  const after = run(); after.metadata.WorkloadSha256 = 'changed'; assert.throws(() => compareRuns(run(), after), /WorkloadSha256/);
  const machine = run(); machine.environment.Architecture = 'Arm64'; assert.throws(() => compareRuns(run(), machine), /environment/);
});
test('rejects missing cases and failed measurements', () => {
  const after = run(); after.rows[0].FullName = 'B'; assert.throws(() => compareRuns(run(), after), /Missing case/);
  const failed = run(); failed.rows[0].Statistics = null; assert.throws(() => compareRuns(run(), failed), /Incomplete/);
});
test('rejects differing warmup and measurement jobs', () => {
  const before = run(), after = run(); before.rows[0].DisplayInfo = 'Job(IterationCount=3, WarmupCount=1)';
  after.rows[0].DisplayInfo = 'Job(IterationCount=10, WarmupCount=5)';
  assert.throws(() => compareRuns(before, after), /measurement job/);
});
