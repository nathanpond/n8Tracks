// A stand-in for tsc, for the tests of scripts/check-canaries.sh. Every line marked
// "// canary: TS<number>" in the project it is run in is reported the way tsc does. A code listed
// in typecheck-off.json is switched off. It exits 2 when it reported anything.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const off = existsSync('typecheck-off.json') ? JSON.parse(readFileSync('typecheck-off.json', 'utf8')) : [];
let errors = 0;

function check(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== 'node_modules') check(path);
      continue;
    }
    if (!/\.tsx?$/.test(entry.name)) continue;
    readFileSync(path, 'utf8')
      .split('\n')
      .forEach((line, index) => {
        const marker = /\/\/ canary: (TS\d+)/.exec(line);
        if (!marker || off.includes(marker[1])) return;
        console.log(`${path}(${index + 1},3): error ${marker[1]}: A stand-in message.`);
        errors += 1;
      });
  }
}

check('.');
process.exit(errors > 0 ? 2 : 0);
