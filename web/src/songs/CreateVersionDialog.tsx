import { Button, Group, Loader, Modal, Radio, Stack, Text, TextInput } from '@mantine/core';
import { useEffect, useState, type SyntheticEvent } from 'react';
import {
  createVersion,
  fetchNextNumbers,
  VERSION_NAME_MAXIMUM_LENGTH,
  type NumberOption,
  type Version,
} from '../api/versions';
import { Notice } from '../components/Notice';
import { nameError, singleLine } from './songRules';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

type Options =
  | { phase: 'loading' }
  | { phase: 'ready'; options: NumberOption[] }
  | { phase: 'too-deep' }
  | { phase: 'failed' };

function proposal(options: NumberOption[]): string | undefined {
  return (options.find((option) => option.proposed) ?? options[0])?.number;
}

function describe(option: NumberOption, source: string): string {
  return option.kind === 'sibling' ? `Next after ${source}` : `Branch under ${source}`;
}

/**
 * The body of the dialog for one source: it asks for the source's next numbers as it opens and
 * preselects the proposal. A number taken meanwhile is refused by the API with fresh options; the
 * proposal among them is selected again and the typed name kept.
 */
function CreateVersionForm({
  songId,
  source,
  onClose,
  onCreated,
}: {
  songId: string;
  source: Version;
  onClose: () => void;
  onCreated: (version: Version) => void;
}) {
  const [options, setOptions] = useState<Options>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [number, setNumber] = useState<string | undefined>();
  const [name, setName] = useState('');
  const [nameProblem, setNameProblem] = useState<string | undefined>();
  const [refusal, setRefusal] = useState<string | undefined>();
  const [failed, setFailed] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    void fetchNextNumbers(source.id, controller.signal).then((result) => {
      if (controller.signal.aborted) {
        return;
      }
      if (result.kind === 'found') {
        setOptions({ phase: 'ready', options: result.options });
        setNumber(proposal(result.options));
      } else {
        setOptions({ phase: result.kind });
      }
    });
    return () => {
      controller.abort();
    };
  }, [source.id, attempt]);

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (number === undefined) {
      return;
    }
    const problem = nameError(name);
    setNameProblem(problem);
    setFailed(false);
    if (problem !== undefined) {
      return;
    }

    setSubmitting(true);
    const result = await createVersion(songId, { sourceVersionId: source.id, number, name });
    setSubmitting(false);

    switch (result.kind) {
      case 'created':
        onCreated(result.version);
        return;
      case 'refused':
        setOptions({ phase: 'ready', options: result.options });
        setNumber(proposal(result.options));
        setRefusal(
          result.taken
            ? `Version ${number} was created elsewhere meanwhile. The numbers below are the ones free now.`
            : `Version ${number} is no longer offered. The numbers below are the ones free now.`,
        );
        return;
      case 'invalid':
        setNameProblem(result.errors.name?.join(' '));
        setFailed(result.errors.name === undefined);
        return;
      case 'failed':
        setFailed(true);
    }
  };

  if (options.phase === 'loading') {
    return <Loader aria-label="Loading the numbers" />;
  }

  if (options.phase === 'too-deep' || options.phase === 'failed') {
    return (
      <Stack gap="md">
        <Notice title="No numbers to offer">
          <Text>
            {options.phase === 'too-deep'
              ? `No Version can be created from ${source.number}: every new number would be longer than 64 characters.`
              : FAILED_MESSAGE}
          </Text>
        </Notice>
        <Group justify="end">
          <Button variant="default" onClick={onClose}>
            Close
          </Button>
          {options.phase === 'failed' && (
            <Button
              onClick={() => {
                setOptions({ phase: 'loading' });
                setAttempt((previous) => previous + 1);
              }}
            >
              Try again
            </Button>
          )}
        </Group>
      </Stack>
    );
  }

  return (
    <form
      noValidate
      aria-label="Create New Version"
      onSubmit={(event) => {
        void submit(event);
      }}
    >
      <Stack gap="md">
        <Text size="sm">
          The new Version starts with a copy of {source.number}’s lyrics and styles. {source.number}{' '}
          itself is not changed.
        </Text>
        <div role="status">
          {refusal !== undefined && (
            <Notice title="That number was taken">
              <Text>{refusal}</Text>
            </Notice>
          )}
        </div>
        <Radio.Group
          label="Number"
          description="Version numbers are assigned by n8Tracks; choose one of these."
          value={number ?? null}
          onChange={setNumber}
        >
          <Stack gap="xs" mt="xs">
            {options.options.map((option) => (
              <Radio
                key={option.number}
                value={option.number}
                label={`${option.number}${option.proposed ? ' (proposed)' : ''}`}
                description={describe(option, source.number)}
              />
            ))}
          </Stack>
        </Radio.Group>
        <TextInput
          label="Name"
          description={`Optional, up to ${VERSION_NAME_MAXIMUM_LENGTH.toLocaleString('en-US')} characters: what this Version tries.`}
          value={name}
          onChange={(event) => {
            setName(singleLine(event.currentTarget.value));
          }}
          error={nameProblem}
          aria-invalid={nameProblem !== undefined}
          data-autofocus
        />
        <div role="status">
          {failed && (
            <Notice title="Version not created">
              <Text>{FAILED_MESSAGE}</Text>
            </Notice>
          )}
        </div>
        <Group justify="end">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={submitting}>
            Create Version
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

/**
 * "Create New Version From" a source Version: the valid numbers (the proposal preselected) and an
 * optional name. `onCreated` gets the new Version, which the API has made the current one.
 */
export function CreateVersionDialog({
  songId,
  source,
  onClose,
  onCreated,
}: {
  songId: string;
  source: Version | undefined;
  onClose: () => void;
  onCreated: (version: Version) => void;
}) {
  return (
    <Modal
      opened={source !== undefined}
      onClose={onClose}
      title={source ? `Create New Version From ${source.number}` : 'Create New Version'}
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      {source !== undefined && (
        <CreateVersionForm
          key={source.id}
          songId={songId}
          source={source}
          onClose={onClose}
          onCreated={onCreated}
        />
      )}
    </Modal>
  );
}
