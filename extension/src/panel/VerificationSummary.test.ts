// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { EntryResult } from '../adapter/fill.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import { GenerateView } from './GenerateView.ts';
import {
  countsText,
  entryDetail,
  entryLabel,
  formName,
  REVIEW_TEXT,
  valueText,
} from './VerificationSummary.ts';

/** One entry of each outcome, as a fill of an Advanced Song reports them. */
const RESULTS: EntryResult[] = [
  { key: 'songs.advanced.model', outcome: 'set', expected: 'v6-mini' },
  {
    key: 'songs.advanced.weirdness',
    outcome: 'failed',
    expected: 70,
    found: 65,
  },
  {
    key: 'songs.advanced.lyrics',
    outcome: 'failed',
    expected: 'one\ntwo',
    found: 'one',
    text: true,
  },
  {
    key: 'songs.advanced.personalize',
    outcome: 'unavailable',
    expected: true,
    note: 'Suno shows the Personalize switch disabled (it may need a paid plan).',
  },
  {
    key: 'songs.advanced.audio',
    outcome: 'manual',
    note: 'Attach the file by hand: the demo recording.',
  },
  { key: 'songs.advanced.inspiration', outcome: 'not_applicable' },
  {
    key: 'songs.advanced.crop',
    outcome: 'unsupported',
    note: 'Suno’s form has no place the extension knows for this value.',
  },
];

function view() {
  const checkAgain = vi.fn<() => void>();
  const generate = new GenerateView(document, {
    create: vi.fn<() => void>(),
    pick: vi.fn<() => void>(),
    checkAgain,
  });
  const main = document.createElement('main');
  main.append(generate.element);
  document.body.append(main);
  return { generate, checkAgain };
}

beforeEach(() => {
  document.title = 'Suno';
  document.documentElement.lang = 'en';
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the verification summary in the panel', () => {
  it('lists every entry with its outcome, and tells the user to review the form and click Create', async () => {
    const { generate } = view();

    generate.show({
      kind: 'verification',
      mode: 'advanced',
      results: RESULTS,
      checkedAt: new Date('2026-10-07T12:00:00Z'),
    });

    const summary = generate.element.querySelector('.verification');
    expect(summary?.querySelector('h4')?.textContent).toBe('Verification: Songs, Advanced');
    expect(summary?.querySelector('[role="status"]')?.textContent).toBe(REVIEW_TEXT);
    expect(
      [...(summary?.querySelectorAll('li') ?? [])].map((item) => [
        item.getAttribute('data-outcome'),
        item.textContent,
      ]),
    ).toEqual([
      ['set', 'Model: Set'],
      ['failed', 'Weirdness: Differs — expected 70, found 65'],
      ['failed', 'Lyrics: Differs — expected 7 characters, found 3 characters'],
      [
        'unavailable',
        'Personalize: Unavailable — Suno shows the Personalize switch disabled (it may need a paid plan).',
      ],
      ['manual', 'Audio: To do by hand — Attach the file by hand: the demo recording.'],
      ['not_applicable', 'Inspo: Not applicable'],
      [
        'unsupported',
        'songs.advanced.crop: Unsupported — Suno’s form has no place the extension knows for this value.',
      ],
    ]);
    expect(summary?.querySelector('.verification-counts')?.textContent).toBe(
      '1 set, 2 differs, 1 unavailable, 1 to do by hand, 1 not applicable, 1 unsupported',
    );
    // Nothing in the summary is a Create control: the user clicks Create in Suno.
    expect(
      [...generate.element.querySelectorAll('button')].map((button) => button.textContent),
    ).toEqual(['Check again']);
    await expectNoAxeViolations(document);
  });

  it('checks again on request, once, with focus on the control', () => {
    const { generate, checkAgain } = view();
    generate.show({
      kind: 'verification',
      mode: 'simple',
      results: RESULTS,
      checkedAt: new Date(),
    });
    const button = generate.element.querySelector<HTMLButtonElement>('.verification-check');

    expect(generate.element.ownerDocument.activeElement).toBe(button);
    button?.click();
    button?.click();

    expect(checkAgain).toHaveBeenCalledOnce();
    expect(button?.disabled).toBe(true);
    expect(generate.element.querySelector('h4')?.textContent).toBe('Verification: Songs, Simple');
  });

  it('names the Speech and Sounds forms and their fields (#147)', async () => {
    const { generate } = view();

    generate.show({
      kind: 'verification',
      form: 'sound',
      mode: 'single',
      results: [
        { key: 'sounds.single.sound_bpm', outcome: 'failed', expected: 400, found: 300 },
        { key: 'sounds.single.sound_scale', outcome: 'not_applicable' },
      ],
      checkedAt: new Date('2026-10-07T12:00:00Z'),
    });

    expect(generate.element.querySelector('h4')?.textContent).toBe('Verification: Sounds');
    expect([...generate.element.querySelectorAll('li')].map((item) => item.textContent)).toEqual([
      'BPM: Differs — expected 400, found 300',
      'Key scale: Not applicable',
    ]);
    await expectNoAxeViolations(document);
    expect(formName('speech', 'simple')).toBe('Speech, Simple');
    expect(formName('song', 'advanced')).toBe('Songs, Advanced');
    expect(entryLabel('speech.advanced.speech_background_music')).toBe('Background music');
    expect(valueText({ key: 'speech.advanced.speech_variety', outcome: 'failed' }, 3)).toBe(
      'extra',
    );
  });

  it('words values: text by its length, toggles On or Off, Variety by its step, nothing as none', () => {
    expect(valueText({ key: 'songs.advanced.title', outcome: 'failed', text: true }, '')).toBe(
      'empty',
    );
    expect(valueText({ key: 'songs.advanced.max_mode', outcome: 'failed' }, false)).toBe('Off');
    expect(valueText({ key: 'songs.advanced.variety', outcome: 'failed' }, 4)).toBe('max');
    expect(valueText({ key: 'songs.advanced.vocal_gender', outcome: 'failed' }, null)).toBe('none');
    expect(entryLabel('songs.simple.simple_prompt')).toBe('Song description');
    expect(
      entryDetail({
        key: 'songs.advanced.styles',
        outcome: 'failed',
        expected: 'abc',
        found: 'xyz',
        text: true,
      }),
    ).toBe('expected the Version’s text, found other text of the same length');
    expect(countsText([])).toBe('');
  });
});
