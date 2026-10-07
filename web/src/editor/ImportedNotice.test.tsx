import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { CreateField } from '../api/createFields';
import { isVersionDetail, type ImportedInputs } from '../api/versions';
import { renderApp } from '../test/helpers';
import { testVersion, versionServer } from '../test/versionServer';
import { importedOptionLabel, outOfRangeText } from './importedInputs';

const IMPORTED: ImportedInputs = {
  notReturned: ['styles', 'vocalGender'],
  outOfRange: ['lyrics', 'variety'],
  rawValues: { variety: '7' },
};

function field(key: string, label: string, option: string | null): CreateField {
  return { key, label, tab: 'songs', modes: ['advanced'], type: 'text', option, help: null };
}

const FIELDS = [
  field('lyrics', 'Lyrics', null),
  field('styles', 'Styles', null),
  field('variety', 'Variety', 'variety'),
  field('vocal_gender', 'Vocal Gender', 'vocalGender'),
];

describe('the imported Version notice', () => {
  it('names each option by its field label, and an unknown choice with what Suno returned', () => {
    expect(importedOptionLabel('lyrics', FIELDS)).toBe('Lyrics');
    expect(importedOptionLabel('vocalGender', FIELDS)).toBe('Vocal Gender');
    expect(importedOptionLabel('notAnOption', FIELDS)).toBe('notAnOption');
    expect(outOfRangeText(IMPORTED, FIELDS)).toBe('Lyrics, Variety (Suno returned 7)');
  });

  it('accepts a Version answer with or without import marks, and refuses malformed ones', () => {
    expect(isVersionDetail(testVersion('1'))).toBe(true);
    expect(isVersionDetail(testVersion('1', { imported: null }))).toBe(true);
    expect(isVersionDetail(testVersion('1', { imported: IMPORTED }))).toBe(true);
    expect(isVersionDetail({ ...testVersion('1'), imported: { notReturned: 'styles' } })).toBe(
      false,
    );
    expect(
      isVersionDetail({
        ...testVersion('1'),
        imported: { ...IMPORTED, rawValues: { variety: 7 } },
      }),
    ).toBe(false);
  });

  it('shows on a frozen imported Version what is out of range and what Suno does not return', async () => {
    versionServer([
      testVersion('1', {
        current: true,
        isFrozen: true,
        revision: 2,
        lyrics: 'l'.repeat(6000),
        imported: IMPORTED,
      }),
    ]);
    renderApp('/songs/n8-7');
    await screen.findByRole('textbox', { name: 'Lyrics' });

    const notice = screen.getByTestId('imported-notice');
    expect(within(notice).getByText('Imported from Suno')).toBeVisible();
    expect(within(notice).getByTestId('imported-out-of-range')).toHaveTextContent(
      "Outside n8Tracks' limits, kept as Suno returned them: Lyrics, Variety (Suno returned 7).",
    );
    expect(within(notice).getByTestId('imported-not-returned')).toHaveTextContent(
      "Not returned by Suno, so shown at n8Tracks' defaults: Styles, Vocal Gender.",
    );
    expect(screen.getByTestId('frozen-notice')).toBeVisible();
  });

  it('does not show on a Version made in n8Tracks', async () => {
    versionServer([testVersion('1', { current: true })]);
    renderApp('/songs/n8-7');
    await screen.findByRole('textbox', { name: 'Lyrics' });

    expect(screen.queryByTestId('imported-notice')).toBeNull();
  });
});
