import { Button, Group, Loader, Modal, Radio, Stack, Text } from '@mantine/core';
import { useEffect, useState } from 'react';
import {
  deleteGeneration,
  fetchGenerationDeletionImpact,
  type Generation,
  type GenerationDeletionImpact,
  type SelectionChoice,
} from '../api/generations';
import { useWorkflowStates, type Song } from '../api/songs';
import { generationDeletionSummary } from './deletionRules';

const FAILED_MESSAGE =
  'Not deleted: n8Tracks did not answer as expected. Check that it is running and try again.';

type Impact =
  | { kind: 'loading' }
  | { kind: 'found'; impact: GenerationDeletionImpact }
  | { kind: 'gone' }
  | { kind: 'failed' };

type ChoiceKind = 'replacement' | 'state';

/**
 * The confirmation for deleting a Generation (#124), opened for `generation` (closed when
 * undefined). It reads what the deletion takes when it opens and states it: the Generation by
 * shortcode with its rating, comments, and Suno artwork; the Versions that use it as a source; that
 * nothing in Suno changes. When it is the Song's Selected Generation it says so and requires a
 * choice first: another of the Song's Generations to select instead (the workflow state stays), or
 * the workflow state to apply once the selection is cleared, the only choice when the Song has no
 * other Generation. Nothing is pre-selected and nothing needs typing. It deletes on the revision that
 * read found; a change since is read again. `onDeleted` gets the Song as it is now.
 */
export function DeleteGenerationDialog({
  generation,
  song,
  onClose,
  onDeleted,
}: {
  generation: Generation | undefined;
  song: Song;
  onClose: () => void;
  onDeleted: (deleted: Generation, song: Song) => void;
}) {
  return (
    <Modal
      opened={generation !== undefined}
      onClose={onClose}
      title={generation === undefined ? 'Delete' : `Delete Generation ${generation.shortcode}?`}
      centered
      size="lg"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      {generation !== undefined && (
        <DeleteForm
          key={generation.id}
          generation={generation}
          song={song}
          onClose={onClose}
          onDeleted={onDeleted}
        />
      )}
    </Modal>
  );
}

function DeleteForm({
  generation,
  song,
  onClose,
  onDeleted,
}: {
  generation: Generation;
  song: Song;
  onClose: () => void;
  onDeleted: (deleted: Generation, song: Song) => void;
}) {
  const [read, setRead] = useState<{ attempt: number; impact: Impact } | undefined>();
  const [attempt, setAttempt] = useState(0);
  const [kind, setKind] = useState<ChoiceKind | undefined>();
  const [replacement, setReplacement] = useState<string | undefined>();
  const [stateId, setStateId] = useState<string | undefined>();
  const [deleting, setDeleting] = useState(false);
  const [message, setMessage] = useState<string | undefined>();
  const states = useWorkflowStates();
  const impact: Impact = read?.attempt === attempt ? read.impact : { kind: 'loading' };

  useEffect(() => {
    const controller = new AbortController();
    void fetchGenerationDeletionImpact(generation.id, controller.signal).then((result) => {
      if (!controller.signal.aborted) {
        setRead({
          attempt,
          impact: result.kind === 'found' ? { kind: 'found', impact: result.impact } : result,
        });
      }
    });
    return () => {
      controller.abort();
    };
  }, [generation.id, attempt]);

  const found = impact.kind === 'found' ? impact.impact : undefined;
  const replacements = found?.replacements ?? [];
  // With no other Generation to select, a workflow state is the only choice.
  const chosenKind: ChoiceKind | undefined = replacements.length === 0 ? 'state' : kind;
  const choice = ((): SelectionChoice | undefined => {
    if (found === undefined) {
      return undefined;
    }
    if (!found.isSelected) {
      return {};
    }
    if (chosenKind === 'replacement' && replacement !== undefined) {
      return { replacementGeneration: replacement };
    }
    if (chosenKind === 'state' && stateId !== undefined) {
      return { workflowState: stateId };
    }
    return undefined;
  })();

  const readAgain = () => {
    setAttempt((previous) => previous + 1);
  };

  const remove = async (target: GenerationDeletionImpact, chosen: SelectionChoice) => {
    setDeleting(true);
    setMessage(undefined);
    const result = await deleteGeneration(target, chosen);
    setDeleting(false);
    switch (result.kind) {
      case 'deleted':
        onDeleted(generation, result.song);
        return;
      case 'conflict':
        setMessage(
          `${target.shortcode} was changed elsewhere since this opened, so it was not deleted. Check what deleting it does now, then choose Delete again.`,
        );
        readAgain();
        return;
      case 'choice-required':
        setMessage(
          `${target.shortcode} has just become the Song’s Selected Generation elsewhere, so it was not deleted. Choose what the Song selects instead, then choose Delete again.`,
        );
        readAgain();
        return;
      case 'invalid':
        setMessage(
          `Not deleted: ${
            result.errors.replacementGeneration?.[0] ??
            result.errors.workflowState?.[0] ??
            'the choice was not accepted.'
          } Check what deleting it does now, then choose again.`,
        );
        setReplacement(undefined);
        setStateId(undefined);
        readAgain();
        return;
      case 'gone':
        setMessage(`${target.shortcode} is no longer there: it may have been deleted elsewhere.`);
        return;
      case 'failed':
        setMessage(FAILED_MESSAGE);
    }
  };

  const visibleStates =
    states.state.phase === 'ready' ? states.state.data.filter((state) => !state.hidden) : [];

  return (
    <Stack gap="md">
      {impact.kind === 'loading' && (
        <Group gap="sm">
          <Loader size="xs" aria-hidden="true" />
          <Text size="sm" role="status">
            Checking what deleting it affects…
          </Text>
        </Group>
      )}
      {found !== undefined && (
        <Stack gap="xs" data-testid="delete-generation-summary">
          {generationDeletionSummary(found).map((line) => (
            <Text key={line}>{line}</Text>
          ))}
        </Stack>
      )}
      {(impact.kind === 'gone' || impact.kind === 'failed') && (
        <Text role="alert" c="var(--mantine-color-error)">
          {impact.kind === 'gone'
            ? `${generation.shortcode} is no longer there: it may have been deleted elsewhere.`
            : 'What deleting it affects could not be checked. Close this and try again.'}
        </Text>
      )}
      {found?.isSelected === true && (
        <Stack gap="sm" data-testid="delete-generation-selection">
          <Text fw={700}>
            {found.shortcode} is the Song’s Selected Generation.{' '}
            {replacements.length === 0
              ? `${song.shortcode} has no other Generation, so choose the workflow state it moves to once the selection is cleared.`
              : `Choose what ${song.shortcode} selects instead.`}
          </Text>
          {replacements.length > 0 && (
            <Radio.Group
              label="Instead"
              value={kind ?? null}
              onChange={(value) => {
                setKind(value === 'state' ? 'state' : 'replacement');
              }}
            >
              <Stack gap="xs" mt="xs">
                <Radio
                  value="replacement"
                  label="Select another Generation (the workflow state stays)"
                />
                <Radio value="state" label="Clear the selection and set a workflow state" />
              </Stack>
            </Radio.Group>
          )}
          {chosenKind === 'replacement' && (
            <Radio.Group
              label="Generation to select instead"
              value={replacement ?? null}
              onChange={setReplacement}
            >
              <Stack gap="xs" mt="xs">
                {replacements.map((other) => (
                  <Radio
                    key={other.id}
                    value={other.id}
                    label={
                      other.title === null ? other.shortcode : `${other.shortcode} (${other.title})`
                    }
                  />
                ))}
              </Stack>
            </Radio.Group>
          )}
          {chosenKind === 'state' && (
            <Radio.Group
              label="Workflow state once the selection is cleared"
              value={stateId ?? null}
              onChange={setStateId}
            >
              <Stack gap="xs" mt="xs">
                {states.state.phase === 'loading' && (
                  <Text size="sm" role="status">
                    Loading the workflow states…
                  </Text>
                )}
                {states.state.phase !== 'loading' && states.state.phase !== 'ready' && (
                  <Text size="sm" role="alert" c="var(--mantine-color-error)">
                    The workflow states could not be loaded. Close this and try again.
                  </Text>
                )}
                {visibleStates.map((state) => (
                  <Radio key={state.id} value={state.id} label={state.name} />
                ))}
              </Stack>
            </Radio.Group>
          )}
        </Stack>
      )}
      {message !== undefined && (
        <Text size="sm" role="alert" c="var(--mantine-color-error)">
          {message}
        </Text>
      )}
      <Group gap="sm" justify="flex-end">
        <Button variant="default" data-autofocus onClick={onClose}>
          Cancel
        </Button>
        <Button
          color="red"
          loading={deleting}
          disabled={found === undefined || choice === undefined}
          onClick={() => {
            if (found !== undefined && choice !== undefined) {
              void remove(found, choice);
            }
          }}
        >
          Delete {generation.shortcode}
        </Button>
      </Group>
    </Stack>
  );
}
