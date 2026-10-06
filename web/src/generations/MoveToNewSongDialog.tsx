import {
  Button,
  Group,
  List,
  Modal,
  NativeSelect,
  Radio,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { useState } from 'react';
import {
  moveGenerationToNewSong,
  type Generation,
  type MoveToNewSongResult,
  type SelectionChoice,
} from '../api/generations';
import { useWorkflowStates, type Song } from '../api/songs';
import { Notice } from '../components/Notice';
import { titleError } from '../songs/songRules';
import { MOVED_WITH_IT, proposedTitle } from './moveRules';

const FAILED_MESSAGE =
  'Not moved: n8Tracks did not answer as expected. Check that it is running and try again.';

type ChoiceKind = 'replacement' | 'state';

/**
 * "Create new Song from Generation" (#123): warns first that the Generation is moved to a new Song,
 * not copied, and names what moves with it; asks the new Song's title (its Suno title, or the Song's,
 * to start with); and, when it is the Song's Selected Generation, what the Song selects instead:
 * another of its Generations, or a workflow state with none selected. Opened while `generation` is
 * set; `others` are the Song's other Generations. `onMoved` gets what n8Tracks answered.
 */
export function MoveToNewSongDialog({
  generation,
  song,
  others,
  onClose,
  onMoved,
}: {
  generation: Generation | undefined;
  song: Song;
  others: readonly Generation[];
  onClose: () => void;
  onMoved: (result: Extract<MoveToNewSongResult, { kind: 'moved' }>) => void;
}) {
  return (
    <Modal
      opened={generation !== undefined}
      onClose={onClose}
      title="Create new Song from Generation"
      closeButtonProps={{ 'aria-label': 'Close' }}
      size="lg"
    >
      {generation !== undefined && (
        <MoveForm
          key={generation.id}
          generation={generation}
          song={song}
          others={others}
          onClose={onClose}
          onMoved={onMoved}
        />
      )}
    </Modal>
  );
}

function MoveForm({
  generation,
  song,
  others,
  onClose,
  onMoved,
}: {
  generation: Generation;
  song: Song;
  others: readonly Generation[];
  onClose: () => void;
  onMoved: (result: Extract<MoveToNewSongResult, { kind: 'moved' }>) => void;
}) {
  const [title, setTitle] = useState(() => proposedTitle(generation, song));
  const [titleProblem, setTitleProblem] = useState<string | undefined>();
  const [kind, setKind] = useState<ChoiceKind>(others.length > 0 ? 'replacement' : 'state');
  const [replacement, setReplacement] = useState(others[0]?.id ?? '');
  const [stateId, setStateId] = useState(song.state.id);
  const [problem, setProblem] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);
  const states = useWorkflowStates();
  const needsChoice = generation.isSelected;

  const choice = (): SelectionChoice => {
    if (!needsChoice) {
      return {};
    }
    return kind === 'replacement'
      ? { replacementGeneration: replacement }
      : { workflowState: stateId };
  };

  const submit = () => {
    const error = titleError(title);
    setTitleProblem(error);
    setProblem(undefined);
    if (error !== undefined) {
      return;
    }
    setBusy(true);
    void moveGenerationToNewSong(generation, title, choice()).then((result) => {
      setBusy(false);
      switch (result.kind) {
        case 'moved':
          onMoved(result);
          return;
        case 'invalid':
          setTitleProblem(result.errors.title?.[0]);
          setProblem(
            result.errors.replacementGeneration?.[0] ??
              result.errors.workflowState?.[0] ??
              (result.errors.title === undefined ? FAILED_MESSAGE : undefined),
          );
          return;
        case 'conflict':
          setProblem(
            `${generation.shortcode} was changed somewhere else at the same time, so it was not moved. Close this and try again.`,
          );
          return;
        case 'choice-required':
          setProblem(
            `${generation.shortcode} has just become the Song’s Selected Generation somewhere else. Close this and try again to choose what the Song selects instead.`,
          );
          return;
        case 'failed':
          setProblem(FAILED_MESSAGE);
          return;
      }
    });
  };

  const stateOptions =
    states.state.phase === 'ready'
      ? states.state.data.map((state) => ({ value: state.id, label: state.name }))
      : [{ value: song.state.id, label: song.state.name }];

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <Stack gap="md">
        <Notice title={`${generation.shortcode} will be moved, not copied`}>
          <Text size="sm" data-testid="move-warning">
            It leaves {song.shortcode} and becomes Generation 1 of the new Song’s Version 1, which
            holds a copy of its Version’s lyrics, styles, Suno options, and sources. These move with
            it:
          </Text>
          <List size="sm" data-testid="move-what-moves">
            {MOVED_WITH_IT.map((item) => (
              <List.Item key={item}>{item}</List.Item>
            ))}
          </List>
          <Text size="sm">Its shortcode changes; {generation.shortcode} will still find it.</Text>
        </Notice>
        <TextInput
          label="New Song title"
          withAsterisk
          value={title}
          error={titleProblem}
          onChange={(event) => {
            setTitle(event.currentTarget.value);
            setTitleProblem(undefined);
          }}
          data-autofocus
        />
        {needsChoice && (
          <Radio.Group
            label={`${generation.shortcode} is the Song’s Selected Generation. What should ${song.shortcode} select instead?`}
            value={kind}
            onChange={(value) => {
              setKind(value === 'state' ? 'state' : 'replacement');
            }}
          >
            <Stack gap="xs" mt="xs">
              <Radio
                value="replacement"
                label="Another Generation"
                disabled={others.length === 0}
              />
              {kind === 'replacement' && others.length > 0 && (
                <NativeSelect
                  label="Generation to select instead"
                  value={replacement}
                  data={others.map((other) => ({
                    value: other.id,
                    label:
                      other.title === null
                        ? other.shortcode
                        : `${other.shortcode} (${other.title})`,
                  }))}
                  onChange={(event) => {
                    setReplacement(event.currentTarget.value);
                  }}
                />
              )}
              <Radio value="state" label="No Selected Generation, and a workflow state" />
              {kind === 'state' && (
                <NativeSelect
                  label="Workflow state for the Song"
                  value={stateId}
                  data={stateOptions}
                  onChange={(event) => {
                    setStateId(event.currentTarget.value);
                  }}
                />
              )}
            </Stack>
          </Radio.Group>
        )}
        {problem !== undefined && (
          <Text role="alert" size="sm" c="var(--mantine-color-error)">
            {problem}
          </Text>
        )}
        <Group justify="flex-end" gap="sm">
          <Button variant="default" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" loading={busy}>
            Move to a new Song
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
