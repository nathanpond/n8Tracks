// A stand-in for ESLint, for the tests of scripts/check-canaries.sh. It "lints" the project it is
// run in: every line marked "// canary: error <rule>" or "// canary: warning <rule>" is reported
// the way ESLint's default formatter does. A rule listed in lint-off.json is switched off. It
// exits 1 on an error, and on a warning only when run with "--max-warnings 0".
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const off = existsSync('lint-off.json') ? JSON.parse(readFileSync('lint-off.json', 'utf8')) : [];
const warningsFail = process.argv.slice(2).join(' ') === '--max-warnings 0';
let errors = 0;
let warnings = 0;

function lint(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== 'node_modules') lint(path);
      continue;
    }
    if (!/\.tsx?$/.test(entry.name)) continue;
    const found = [];
    readFileSync(path, 'utf8')
      .split('\n')
      .forEach((line, index) => {
        const marker = /\/\/ canary: (error|warning) (\S+)/.exec(line);
        if (!marker || off.includes(marker[2])) return;
        found.push(`  ${index + 1}:3  ${marker[1]}  A stand-in message  ${marker[2]}`);
        if (marker[1] === 'error') errors += 1;
        else warnings += 1;
      });
    if (found.length > 0) console.log(`\n${resolve(path)}\n${found.join('\n')}`);
  }
}

lint('.');
if (errors + warnings > 0) console.log(`\n${errors + warnings} problems (${errors} errors, ${warnings} warnings)\n`);
process.exit(errors > 0 || (warnings > 0 && warningsFail) ? 1 : 0);
