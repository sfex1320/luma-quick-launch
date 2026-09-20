import { z } from 'zod';
import { StateSchema, resultSchemas } from '../src/contracts.ts';
import { defaults } from '../src/data.ts';
import { writeFile, mkdir } from 'node:fs/promises';
await mkdir('docs/contracts', { recursive: true });
await writeFile('docs/contracts/state.schema.json', JSON.stringify(z.toJSONSchema(StateSchema), null, 2));
await writeFile('docs/contracts/results.schema.json', JSON.stringify(Object.fromEntries(Object.entries(resultSchemas).map(([name, schema]) => [name, z.toJSONSchema(schema)])), null, 2));
await writeFile('docs/contracts/empty-state.json', JSON.stringify({ schemaVersion: 1, revision: 0, projects: [], preferences: defaults }, null, 2));
console.log('Generated state schema, result schemas and empty native state.');
