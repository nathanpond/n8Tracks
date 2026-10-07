import {
  Anchor,
  Button,
  Group,
  Loader,
  NativeSelect,
  Radio,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { useEffect, useState } from 'react';
import type { Song } from '../api/songs';
import {
  readImportTargets,
  type ImportChoice,
  type ImportRecord,
  type ImportTargets,
} from '../api/sunoImports';
import { SongSearch } from '../common/SongSearch';

type Action = 'import' | 'ignore' | 'skip';

/** The Song a new import goes to: a new one, titled, or an existing one, found by search. */
type SongChoice =
  | { kind: 'new' }
  | { kind: 'existing'; song: Pick<Song, 'id' | 'shortcode' | 'title'> | undefined };

/** A matching Version (by ID), or a new Version. */
const NEW_VERSION = 'new';

/** The parent value for a new top-level Version. */
const TOP_LEVEL = '';

/**
 * The choice to give the selected records: import them (to a new Song, or to an existing Song: a
 * Version the server says holds their inputs, or a new Version with a number from the valid options),
 * Don't copy, or Skip this time. The Versions and numbers are asked of the server for `sample`, a selected
 * record on the page (the server checks every selected record when the choice is applied). Applying
 * calls `onApply`; nothing is saved before that, and the server refuses the whole change if any record
 * cannot take the choice.
 */
export function ChoiceEditor({
  exportId,
  sample,
  nextKey,
  count,
  busy,
  onApply,
}: {
  exportId: string;
  /** A selected record on this page, or (when none is) another record the review can change. */
  sample: ImportRecord | undefined;
  /** A temporary key no choice uses, for a new Song or Version. */
  nextKey: string;
  /** What is selected, in words ("3 records"). */
  count: string;
  busy: boolean;
  onApply: (choice: ImportChoice) => void;
}) {
  const [action, setAction] = useState<Action>('import');
  const [songChoice, setSongChoice] = useState<SongChoice>({ kind: 'new' });
  const [title, setTitle] = useState(sample?.title ?? '');
  const [version, setVersion] = useState(NEW_VERSION);
  const [parent, setParent] = useState(TOP_LEVEL);
  const [number, setNumber] = useState('');
  // The answer for the request it answers (the Song, the sample, and the parent), so a new request shows as loading.
  const [answered, setAnswered] = useState<{
    request: string;
    targets: ImportTargets | 'failed';
  }>();

  const song = songChoice.kind === 'existing' ? songChoice.song : undefined;
  const existing = songChoice.kind === 'existing';
  const sampleId = sample?.sunoId;
  const request =
    song === undefined || sampleId === undefined
      ? undefined
      : JSON.stringify([song.shortcode, sampleId, parent]);
  const targets =
    request === undefined
      ? undefined
      : answered?.request === request
        ? answered.targets
        : 'loading';

  // The Versions holding the sample's inputs, and the numbers a new Version may take under the parent.
  useEffect(() => {
    if (song === undefined || sampleId === undefined || request === undefined) {
      return;
    }
    const controller = new AbortController();
    void readImportTargets(
      exportId,
      sampleId,
      song.shortcode,
      parent === TOP_LEVEL ? null : parent,
      controller.signal,
    ).then((found) => {
      if (controller.signal.aborted) {
        return;
      }
      setAnswered({ request, targets: found ?? 'failed' });
      setNumber(found?.numbers[0]?.number ?? '');
    });
    return () => {
      controller.abort();
    };
  }, [exportId, sampleId, song, parent, request]);

  const loaded = typeof targets === 'object' ? targets : undefined;
  const reimport = sample?.class === 'deleted';
  const choice = (): ImportChoice | undefined => {
    if (action !== 'import') {
      return { action };
    }
    if (!existing) {
      const trimmed = title.trim();
      return trimmed === ''
        ? undefined
        : {
            action: 'import',
            target: {
              kind: 'newSong',
              key: nextKey,
              title: trimmed,
              workspaceId: sample?.workspaceId ?? null,
            },
          };
    }
    if (song === undefined) {
      return undefined;
    }
    if (version !== NEW_VERSION) {
      return { action: 'import', target: { kind: 'version', version } };
    }
    return number === ''
      ? undefined
      : {
          action: 'import',
          target: {
            kind: 'newVersion',
            key: nextKey,
            song: song.id,
            parentVersion: parent === TOP_LEVEL ? null : parent,
            number,
          },
        };
  };
  const ready = choice();

  return (
    <Stack gap="sm" role="group" aria-label="Choice for the selected records">
      <Radio.Group
        label={`What to do with ${count}`}
        value={action}
        onChange={(value) => {
          setAction(value);
        }}
      >
        <Group gap="md" mt={4}>
          <Radio value="import" label={reimport ? 'Reimport' : 'Import'} />
          <Radio value="ignore" label="Don’t copy" />
          <Radio value="skip" label="Skip this time" />
        </Group>
      </Radio.Group>
      {action === 'ignore' && (
        <Text size="sm">Don’t copy puts them on the ignore list when you confirm.</Text>
      )}
      {action === 'skip' && (
        <Text size="sm">
          Skip this time stores nothing about them: the next sync offers them again.
        </Text>
      )}
      {action === 'import' && (
        <>
          <Radio.Group
            label="Song"
            value={songChoice.kind}
            onChange={(value) => {
              setSongChoice(
                value === 'existing' ? { kind: 'existing', song: undefined } : { kind: 'new' },
              );
              setVersion(NEW_VERSION);
              setParent(TOP_LEVEL);
            }}
          >
            <Group gap="md" mt={4}>
              <Radio value="new" label="A new Song" />
              <Radio value="existing" label="An existing Song" />
            </Group>
          </Radio.Group>
          {songChoice.kind === 'new' && (
            <TextInput
              label="New Song title"
              value={title}
              onChange={(event) => {
                setTitle(event.currentTarget.value);
              }}
              error={title.trim() === '' ? 'Enter a title.' : undefined}
            />
          )}
          {existing && song === undefined && (
            <SongSearch
              label="Find the Song"
              onChoose={(found) => {
                setSongChoice({
                  kind: 'existing',
                  song: { id: found.id, shortcode: found.shortcode, title: found.title },
                });
              }}
            />
          )}
          {song !== undefined && (
            <>
              <Group gap="sm">
                <Text size="sm" data-testid="chosen-song">
                  Song {song.shortcode} “{song.title}”
                </Text>
                <Anchor
                  component="button"
                  type="button"
                  size="sm"
                  underline="always"
                  onClick={() => {
                    setSongChoice({ kind: 'existing', song: undefined });
                    setVersion(NEW_VERSION);
                    setParent(TOP_LEVEL);
                  }}
                >
                  Choose another Song
                </Anchor>
              </Group>
              {targets === 'loading' && (
                <Loader size="sm" aria-label="Loading the Song’s Versions" />
              )}
              {targets === 'failed' && (
                <Text size="sm" role="alert">
                  The Song’s Versions could not be loaded. Choose the Song again.
                </Text>
              )}
              {sampleId === undefined && (
                <Text size="sm">Select a record on this page to see which Versions match it.</Text>
              )}
              {loaded !== undefined && (
                <Radio.Group
                  label="Version"
                  value={version}
                  onChange={(value) => {
                    setVersion(value);
                  }}
                >
                  <Stack gap={4} mt={4}>
                    {loaded.matching.map((match) => (
                      <Radio
                        key={match.id}
                        value={match.id}
                        label={`Version ${match.shortcode}, which holds the same creation inputs`}
                      />
                    ))}
                    <Radio value={NEW_VERSION} label="A new Version" />
                  </Stack>
                </Radio.Group>
              )}
              {loaded?.matching.length === 0 && (
                <Text size="sm" data-testid="no-matching-version">
                  No Version of this Song holds the same creation inputs.
                </Text>
              )}
              {loaded !== undefined && version === NEW_VERSION && (
                <Group gap="sm" align="flex-end">
                  <NativeSelect
                    label="Branch from"
                    value={parent}
                    data={[
                      { value: TOP_LEVEL, label: 'Nothing: a new top-level Version' },
                      ...loaded.versions.map((other) => ({
                        value: other.id,
                        label: `Version ${other.shortcode}`,
                      })),
                    ]}
                    onChange={(event) => {
                      setParent(event.currentTarget.value);
                    }}
                  />
                  <NativeSelect
                    label="Version number"
                    value={number}
                    data={
                      loaded.numbers.length === 0
                        ? [{ value: '', label: 'No number is free' }]
                        : loaded.numbers.map((option) => ({
                            value: option.number,
                            label: option.proposed ? `${option.number} (proposed)` : option.number,
                          }))
                    }
                    onChange={(event) => {
                      setNumber(event.currentTarget.value);
                    }}
                  />
                </Group>
              )}
            </>
          )}
        </>
      )}
      <div>
        <Button
          loading={busy}
          disabled={ready === undefined}
          onClick={() => {
            if (ready !== undefined) {
              onApply(ready);
            }
          }}
        >
          Apply to {count}
        </Button>
      </div>
    </Stack>
  );
}
