import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';

// Optional external validators live in ignored artifacts; project dependency manifests remain unchanged.
const fixtures = path.resolve(process.argv[2] ?? 'artifacts/schema-fixtures');
const dependencies = path.resolve(process.argv[3] ?? 'artifacts/schema-validation-tools');
const require = createRequire(path.join(dependencies, 'package.json'));
const ts = require('typescript');
const protobuf = require('protobufjs');
const Ajv2020 = require('ajv/dist/2020').default;
const addFormats = require('ajv-formats');

const config = path.join(fixtures, 'tsconfig.json');
fs.writeFileSync(config, JSON.stringify({ compilerOptions: {
  strict: true, noEmit: true, target: 'ES2020', module: 'NodeNext', moduleResolution: 'NodeNext', types: [],
}, files: ['model.ts', 'instances.ts'] }, null, 2));
const compiler = path.join(path.dirname(require.resolve('typescript/package.json')), 'bin', 'tsc');
const checked = spawnSync(process.execPath, [compiler, '--project', config], { encoding: 'utf8' });
if (checked.error) throw checked.error;
if (checked.status !== 0) throw new Error(checked.stdout + checked.stderr);
const proto = protobuf.parse(fs.readFileSync(path.join(fixtures, 'model.proto'), 'utf8'), { keepCase: true }).root;
proto.resolveAll();
if (!proto.lookupType('object_analyzer.generated.GeneratedSchema')) throw new Error('Missing root message');
const ajv = new Ajv2020({ strict: false, allErrors: true });
addFormats(ajv);
const validate = ajv.compile(JSON.parse(fs.readFileSync(path.join(fixtures, 'model.schema.json'), 'utf8')));
const samples = JSON.parse(fs.readFileSync(path.join(fixtures, 'samples.json'), 'utf8'));
for (const sample of samples) if (!validate(sample)) throw new Error(JSON.stringify(validate.errors, null, 2));
process.stdout.write(JSON.stringify({ typescript: ts.version, typescriptDiagnostics: 0,
  protobuf: 'parsed and all types resolved', jsonSchema: 'draft 2020-12', samplesValidated: samples.length }) + '\n');
