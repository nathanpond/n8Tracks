import assert from 'node:assert/strict';
import { test } from 'node:test';
import { missingReportMarkdown, summarize, toMarkdown, type Report } from './summarize.ts';

/** The shape Playwright's JSON reporter writes: a file suite, a describe suite, specs per project. */
function report(statuses: Record<string, string[]>, errors: Report['errors'] = []): Report {
  return {
    suites: [
      {
        title: 'shell.spec.ts',
        suites: [
          {
            title: 'the shell page',
            specs: Object.entries(statuses).map(([title, projects], index) => ({
              title,
              file: 'shell.spec.ts',
              line: 10 + index,
              tests: projects.map((status, project) => ({
                projectName: project === 0 ? 'root' : 'subpath',
                status,
              })),
            })),
          },
        ],
      },
    ],
    errors,
  };
}

void test('a clean run has no failed and no flaky tests, and says how many passed', () => {
  const summary = summarize(report({ loads: ['expected', 'expected'], switches: ['expected'] }));

  assert.deepEqual(summary.failed, []);
  assert.deepEqual(summary.flaky, []);
  assert.equal(summary.ran, 3);
  assert.match(toMarkdown(summary), /All 3 tests passed on the first attempt\./);
});

void test('a test that passed only on the retry is listed as flaky, with its project and place', () => {
  const summary = summarize(report({ loads: ['expected', 'flaky'], switches: ['expected'] }));

  assert.deepEqual(summary.failed, []);
  assert.deepEqual(summary.flaky, ['[subpath] shell.spec.ts:10 › the shell page › loads']);
  const markdown = toMarkdown(summary);
  assert.match(markdown, /Flaky: failed, then passed on the retry \(1\):/);
  assert.match(markdown, /- \[subpath\] shell\.spec\.ts:10 › the shell page › loads/);
  assert.doesNotMatch(markdown, /Failed/);
  assert.doesNotMatch(markdown, /passed on the first attempt/);
});

void test('a test that failed on every attempt is listed as failed, not as flaky', () => {
  const summary = summarize(report({ loads: ['unexpected', 'expected'], switches: ['flaky'] }));

  assert.deepEqual(summary.failed, ['[root] shell.spec.ts:10 › the shell page › loads']);
  assert.deepEqual(summary.flaky, ['[root] shell.spec.ts:11 › the shell page › switches']);
  assert.match(toMarkdown(summary), /Failed \(1\):/);
});

void test('a skipped test is neither counted nor listed', () => {
  const summary = summarize(report({ loads: ['skipped', 'expected'] }));

  assert.equal(summary.ran, 1);
  assert.deepEqual(summary.failed, []);
  assert.deepEqual(summary.flaky, []);
});

void test('an error outside any test is reported by its first line, without colour codes', () => {
  const summary = summarize(
    report({}, [
      { message: '\u001b[31mError: Host port 18787 is already in use\u001b[39m\n  at x' },
    ]),
  );

  assert.deepEqual(summary.errors, ['Error: Host port 18787 is already in use']);
  const markdown = toMarkdown(summary);
  assert.match(markdown, /errors outside any test/);
  assert.doesNotMatch(markdown, /passed on the first attempt/);
});

void test('a missing report is explained instead of being read as a clean run', () => {
  const markdown = missingReportMarkdown('test-results/results.json');

  assert.match(markdown, /No test report was written \(test-results\/results\.json\)/);
  assert.doesNotMatch(markdown, /passed/);
});
