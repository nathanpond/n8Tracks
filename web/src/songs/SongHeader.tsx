import {
  Anchor,
  Button,
  Group,
  Menu,
  Paper,
  Stack,
  Text,
  TextInput,
  Textarea,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import { useState, type ClipboardEvent, type KeyboardEvent, type ReactNode } from 'react';
import { Link } from 'react-router';
import type { FieldValue } from '../api/saves';
import { kindLabel } from '../api/versions';
import { CONCEPT_MAXIMUM_LENGTH, type Song, type WorkflowState } from '../api/songs';
import { ArtworkImage } from '../common/ArtworkImage';
import { ShortcodeBadge } from '../common/ShortcodeBadge';
import { SelectedGenerationBadges } from '../generations/GenerationParts';
import { focusOnMount, saveError, useInPlaceEdit } from '../common/useInPlaceEdit';
import type { SaveOutcome } from '../common/useRevisionedSave';
import { DuplicateTitleIndicator } from './DuplicateTitleIndicator';
import { UnavailableWorkspaceBadge } from './details/WorkspaceSection';
import { StateBadge, TagLabels } from './SongParts';
import { conceptError, singleLine, titleError } from './songRules';

/** The title as it is saved: one line, trimmed. */
function normaliseTitle(draft: string): string {
  return singleLine(draft).trim();
}

/** The concept as it is saved: line endings as `\n`, trimmed, and null when nothing is left. */
function normaliseConcept(draft: string): FieldValue {
  const normalised = draft.replace(/\r\n|\r/g, '\n').trim();
  return normalised === '' ? null : normalised;
}

/**
 * The title: the page's h2, edited in place on one line. Enter or blur saves; Escape cancels. Beside
 * it, how many other Songs share it.
 */
function TitleField({
  song,
  save,
}: {
  song: Song;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
}) {
  const edit = useInPlaceEdit({
    field: 'title',
    current: song.title,
    normalise: normaliseTitle,
    check: titleError,
    save,
  });

  if (!edit.editing) {
    return (
      <Group gap="sm" align="center">
        <Title order={2} style={{ overflowWrap: 'anywhere' }}>
          {song.title}
        </Title>
        <Button variant="subtle" size="compact-sm" onClick={edit.start} aria-label="Edit title">
          Edit
        </Button>
        <DuplicateTitleIndicator key={song.id} song={song} />
      </Group>
    );
  }

  const paste = (event: ClipboardEvent<HTMLInputElement>) => {
    const pasted = event.clipboardData.getData('text');
    if (!/[\r\n]/.test(pasted)) {
      return;
    }
    // A text field would drop the line breaks, running the words together; spaces keep them apart.
    event.preventDefault();
    const input = event.currentTarget;
    const start = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? start;
    edit.setDraft(input.value.slice(0, start) + singleLine(pasted) + input.value.slice(end));
  };

  const keys = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      void edit.commit();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      edit.cancel();
    }
  };

  return (
    <TextInput
      label="Title"
      description="Enter saves, Escape cancels."
      size="md"
      value={edit.draft}
      onChange={(event) => {
        edit.setDraft(singleLine(event.currentTarget.value));
      }}
      onPaste={paste}
      onKeyDown={keys}
      onBlur={() => {
        void edit.commit();
      }}
      error={edit.error}
      aria-invalid={edit.error !== undefined}
      readOnly={edit.saving}
      ref={focusOnMount}
    />
  );
}

/** The concept: several lines, edited in place. Blur or Ctrl/Cmd+Enter saves; Escape cancels. */
function ConceptField({
  song,
  save,
}: {
  song: Song;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
}) {
  const edit = useInPlaceEdit({
    field: 'concept',
    current: song.concept,
    normalise: normaliseConcept,
    check: conceptError,
    save,
  });

  const keys = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void edit.commit();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      edit.cancel();
    }
  };

  return (
    <Paper p="md" withBorder component="section" aria-labelledby="song-concept">
      <Stack gap={4}>
        <Group gap="sm" align="center">
          <Title order={3} size="h5" id="song-concept">
            Concept
          </Title>
          {!edit.editing && (
            <Button
              variant="subtle"
              size="compact-sm"
              onClick={edit.start}
              aria-label="Edit concept"
            >
              Edit
            </Button>
          )}
        </Group>
        {edit.editing ? (
          <Textarea
            aria-labelledby="song-concept"
            description={`Up to ${CONCEPT_MAXIMUM_LENGTH.toLocaleString('en-US')} characters. Ctrl+Enter or Cmd+Enter saves, Escape cancels; leave it empty to clear it.`}
            rows={6}
            resize="vertical"
            value={edit.draft}
            onChange={(event) => {
              edit.setDraft(event.currentTarget.value);
            }}
            onKeyDown={keys}
            onBlur={() => {
              void edit.commit();
            }}
            error={edit.error}
            aria-invalid={edit.error !== undefined}
            readOnly={edit.saving}
            ref={focusOnMount}
          />
        ) : song.concept === null ? (
          <Text c="var(--n8-color-secondary-text)">No concept yet.</Text>
        ) : (
          <Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{song.concept}</Text>
        )}
      </Stack>
    </Paper>
  );
}

/**
 * The states the menu offers: every visible state and, while the Song is in a hidden one, that
 * one too, in workflow order.
 */
function offeredStates(states: WorkflowState[], currentId: string): WorkflowState[] {
  return states.filter((state) => !state.hidden || state.id === currentId);
}

/** The workflow state, changed from a menu. A Song may move from any state to any other. */
function StateField({
  song,
  states,
  save,
}: {
  song: Song;
  states: WorkflowState[] | undefined;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
}) {
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const offered = offeredStates(states ?? [], song.state.id);

  const choose = async (id: string) => {
    if (id === song.state.id) {
      return;
    }
    setSaving(true);
    setError(undefined);
    const outcome = await save('stateId', id);
    setSaving(false);
    setError(saveError(outcome, 'stateId'));
  };

  return (
    <Stack gap={2}>
      {/* No focus placeholder: Mantine's is a focusable element with role="presentation" inside
          the menu, which axe reports as a child a menu may not have (aria-required-children).
          Not hidden when the button scrolls out of view: the menu is short-lived, and the check
          closes it at once where nothing is laid out (jsdom). */}
      <Menu
        position="bottom-start"
        withinPortal
        withInitialFocusPlaceholder={false}
        hideDetached={false}
      >
        <Menu.Target>
          <Button
            variant="default"
            size="compact-sm"
            loading={saving}
            disabled={states === undefined}
            rightSection={<span aria-hidden="true">▾</span>}
          >
            <VisuallyHidden>State: </VisuallyHidden>
            <StateBadge name={song.state.name} colour={song.state.colour} />
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>Move to</Menu.Label>
          {offered.map((state) => (
            <Menu.Item
              key={state.id}
              onClick={() => {
                void choose(state.id);
              }}
              rightSection={state.id === song.state.id ? '✓' : undefined}
            >
              <StateBadge name={state.name} colour={state.colour} />
              {state.id === song.state.id && <VisuallyHidden> (current)</VisuallyHidden>}
              {state.hidden && <VisuallyHidden> (hidden)</VisuallyHidden>}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>
      {error !== undefined && (
        <Text size="sm" c="var(--mantine-color-error)" role="alert">
          {error}
        </Text>
      )}
    </Stack>
  );
}

/**
 * The Song's Selected Generation (#120): its shortcode, linking to its panel, with badges when it is
 * Archived, in Suno's Trash, or missing from Suno; or that the Song has none.
 */
function SelectedGenerationField({ song }: { song: Song }) {
  const selected = song.selectedGeneration;
  return (
    <Group gap={6} wrap="wrap" data-testid="song-selected-generation">
      <Text size="sm">Selected Generation:</Text>
      {selected === null ? (
        <Text size="sm" c="var(--n8-color-secondary-text)">
          None
        </Text>
      ) : (
        <>
          <Anchor
            component={Link}
            to={`/songs/${song.shortcode}/generations/${selected.shortcode}`}
            size="sm"
            ff="monospace"
            underline="always"
            aria-label={`Selected Generation ${selected.shortcode}`}
          >
            {selected.shortcode}
          </Anchor>
          <SelectedGenerationBadges selected={selected} />
        </>
      )}
    </Group>
  );
}

/**
 * The Song page's header: its artwork (chosen in the Details panel; a placeholder when it has
 * none), shortcode, title, workflow state, and concept, each edited where it is
 * shown, the Song's Tags as coloured labels (chosen in the Details panel), and `actions` (the
 * Details control) and `play` (the Song's Play control, #219) beside the shortcode, its Selected Generation with a link to it, and a badge when
 * its Suno workspace is unavailable (#129). Every save goes through the
 * page's one `useRevisionedSave` (`save`), so a save based on an old revision is refused and offered
 * for comparison and reapplying instead of overwriting.
 */
export function SongHeader({
  song,
  states,
  save,
  actions,
  play,
}: {
  song: Song;
  /** Every workflow state, or undefined while they load. */
  states: WorkflowState[] | undefined;
  save: (key: string, value: FieldValue) => Promise<SaveOutcome>;
  actions?: ReactNode;
  /** The Song's Play control (#219), beside its shortcode. */
  play?: ReactNode;
}) {
  return (
    <>
      <Group gap="md" align="flex-start" wrap="nowrap">
        <div data-testid="song-header-artwork">
          <ArtworkImage artwork={song.artwork} title={song.title} size="320" pixels={96} />
        </div>
        <Stack gap={4} style={{ flex: '1 1 0', minWidth: 0 }}>
          <Group gap="sm" align="center" wrap="wrap">
            <ShortcodeBadge shortcode={song.shortcode} />
            {play}
            <Text size="sm" data-testid="song-kind">
              Kind: {kindLabel(song.currentVersion.kind)}
            </Text>
            <SelectedGenerationField song={song} />
            {song.sunoWorkspace?.state === 'unavailable' && (
              <UnavailableWorkspaceBadge workspace={song.sunoWorkspace} />
            )}
            {actions !== undefined && <div style={{ marginInlineStart: 'auto' }}>{actions}</div>}
          </Group>
          <Group gap="md" align="flex-start" wrap="wrap">
            <div style={{ flex: '1 1 20rem', minWidth: 0 }}>
              <TitleField song={song} save={save} />
            </div>
            <StateField song={song} states={states} save={save} />
          </Group>
          {song.tags.length > 0 && (
            <div role="group" aria-label="Tags" data-testid="song-tags">
              <TagLabels tags={song.tags} />
            </div>
          )}
        </Stack>
      </Group>
      <ConceptField song={song} save={save} />
    </>
  );
}
