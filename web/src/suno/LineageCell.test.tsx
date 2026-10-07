import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { ImportLineageSource, ImportRecord } from '../api/sunoImports';
import { renderApp } from '../test/helpers';
import { EXPORT_ID, importServer, testRecord, toNewSong } from '../test/importServer';

/** A source of the type `action` naming `sunoId`. */
function source(sunoId: string, change: Partial<ImportLineageSource> = {}): ImportLineageSource {
  return {
    group: 'audio',
    typeId: '01a10a6e-de00-7000-8000-000000000001',
    typeName: 'Cover',
    sunoAction: 'cover',
    sunoId,
    title: `Song ${sunoId}`,
    continueAtSeconds: null,
    place: 'not_imported',
    generation: null,
    record: null,
    ...change,
  };
}

/** `record` as a source names it: in this export, with its choice. */
function inExport(record: ImportRecord): Partial<ImportLineageSource> {
  return {
    place: 'export',
    title: record.title ?? 'Untitled',
    record: {
      sunoId: record.sunoId,
      title: record.title,
      workspaceId: record.workspaceId,
      class: record.class,
      choice: record.choice,
      proposal: record.proposal,
    },
  };
}

function row(sunoId: string): HTMLElement {
  const found = document.querySelector<HTMLElement>(`tr[data-record="${sunoId}"]`);
  if (found === null) {
    throw new Error(`No row for ${sunoId}`);
  }
  return found;
}

function lineageOf(sunoId: string): string[] {
  return within(row(sunoId))
    .queryAllByTestId('lineage-line')
    .map((line) => line.textContent);
}

async function openReview() {
  renderApp(`/suno/imports/${EXPORT_ID}`);
  expect(await screen.findByRole('table', { name: 'Records in this import' })).toBeVisible();
}

describe('the review’s Lineage column', () => {
  it('sits after the title and says what each record was made from', async () => {
    const parentHeld = source('held', {
      place: 'generation',
      title: 'Held parent',
      generation: { id: 'g-1', shortcode: 'n8-3-v1-g1', songShortcode: 'n8-3' },
    });
    importServer([
      testRecord('cover', {
        title: 'A cover',
        lineage: { sources: [source('gone')], playlist: null, voice: null },
      }),
      testRecord('mashup', {
        title: 'A mashup',
        lineage: {
          sources: [
            { ...parentHeld, typeName: 'Mashup', sunoAction: 'mashup' },
            source('other', { typeName: 'Mashup', sunoAction: 'mashup' }),
          ],
          playlist: null,
          voice: null,
        },
      }),
      testRecord('voiced', {
        title: 'Voiced',
        lineage: {
          sources: [
            source('insp', {
              group: 'inspiration',
              typeName: 'Use as Inspiration',
              sunoAction: 'inspiration',
            }),
          ],
          playlist: null,
          voice: { personaId: 'pe-1', name: 'Smoky' },
        },
      }),
      testRecord('extended', {
        title: 'Extended',
        lineage: {
          sources: [
            source('base', { typeName: 'Extend', sunoAction: 'extend', continueAtSeconds: 61 }),
          ],
          playlist: null,
          voice: null,
        },
      }),
      testRecord('plain', { title: 'Plain', lineage: null }),
    ]);
    await openReview();

    const headers = within(screen.getByRole('table', { name: 'Records in this import' }))
      .getAllByRole('columnheader')
      .map((header) => header.textContent);
    expect(headers.indexOf('Lineage')).toBe(headers.indexOf('Title') + 1);

    expect(lineageOf('cover')).toEqual(['Cover of Song gone: not in this sync']);
    expect(lineageOf('mashup')).toEqual(['Mashup of Held parent + Song other (not in this sync)']);
    expect(
      within(row('mashup')).getByRole('link', { name: 'Held parent: Generation n8-3-v1-g1' }),
    ).toHaveAttribute('href', '/songs/n8-3/generations/n8-3-v1-g1');
    expect(lineageOf('voiced')).toEqual([
      'Inspired by Song insp: not in this sync',
      'Voice: Smoky',
    ]);
    expect(lineageOf('extended')).toEqual(['Extension of Song base at 1:01: not in this sync']);
    expect(within(row('plain')).queryByTestId('record-lineage')).toBeNull();

    // Complement: a source not in this export cannot be included.
    expect(screen.queryByRole('button', { name: /^Include / })).toBeNull();
  });

  it('includes a source in this export by changing that record’s choice only', async () => {
    const user = userEvent.setup();
    const parent = testRecord('parent', {
      title: 'The original',
      proposal: {
        choice: {
          action: 'import',
          target: { kind: 'newSong', key: 'new:5', title: 'The original', workspaceId: 'studio' },
        },
        basis: 'newSong',
        group: null,
      },
      choice: { action: 'skip' },
    });
    const cover = toNewSong(
      testRecord('cover', {
        title: 'My cover',
        lineage: { sources: [source('parent', inExport(parent))], playlist: null, voice: null },
      }),
      'new:1',
      'My cover',
    );
    const server = importServer([cover, parent, testRecord('bystander', { title: 'Bystander' })]);
    await openReview();

    expect(lineageOf('cover')).toEqual([
      'Cover of The original: in this sync, not chosen for import',
    ]);
    await user.click(
      within(row('cover')).getByRole('button', { name: 'Include The original in this import' }),
    );

    await waitFor(() => {
      expect(server.patches).toHaveLength(1);
    });
    expect(server.patches[0]?.body).toEqual({
      sunoIds: ['parent'],
      choice: {
        action: 'import',
        target: { kind: 'newSong', key: 'new:5', title: 'The original', workspaceId: 'studio' },
      },
    });
    expect(await screen.findByText('The choice was saved.')).toBeVisible();
    await waitFor(() => {
      expect(lineageOf('cover')).toEqual(['Cover of The original: in this sync']);
    });
    expect(within(row('cover')).queryByRole('button', { name: /^Include / })).toBeNull();
    // Only that record changed.
    expect(server.records.find((record) => record.sunoId === 'bystander')?.choice).toEqual({
      action: 'skip',
    });
    expect(server.records.find((record) => record.sunoId === 'cover')?.choice).toEqual(
      cover.choice,
    );
  });
});
