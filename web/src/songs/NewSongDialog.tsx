import { Button, Group, Modal, Stack, Text, TextInput, Textarea } from '@mantine/core';
import { useState, type ClipboardEvent, type SyntheticEvent } from 'react';
import { CONCEPT_MAXIMUM_LENGTH, createSong, TITLE_MAXIMUM_LENGTH, type Song } from '../api/songs';
import { Notice } from '../components/Notice';
import { conceptError, singleLine, titleError } from './songRules';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/**
 * Asks for a new Song's title and concept and creates it. The fields are checked as the API checks
 * them before anything is sent, and the API's own field errors are shown the same way. Anything
 * else that goes wrong keeps the dialog open with what was typed. `onCreated` gets the new Song.
 */
export function NewSongDialog({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (song: Song) => void;
}) {
  const [title, setTitle] = useState('');
  const [concept, setConcept] = useState('');
  const [errors, setErrors] = useState<{ title?: string; concept?: string }>({});
  const [failed, setFailed] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const close = () => {
    setTitle('');
    setConcept('');
    setErrors({});
    setFailed(false);
    onClose();
  };

  const pasteTitle = (event: ClipboardEvent<HTMLInputElement>) => {
    const pasted = event.clipboardData.getData('text');
    if (!/[\r\n]/.test(pasted)) {
      return;
    }
    // A text field would drop the line breaks, running the words together; spaces keep them apart.
    event.preventDefault();
    const input = event.currentTarget;
    const start = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? start;
    const replacement = singleLine(pasted);
    setTitle(input.value.slice(0, start) + replacement + input.value.slice(end));
  };

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    const local = { title: titleError(title), concept: conceptError(concept) };
    setFailed(false);
    if (local.title !== undefined || local.concept !== undefined) {
      setErrors(local);
      return;
    }

    setSubmitting(true);
    const result = await createSong({ title: singleLine(title), concept });
    setSubmitting(false);

    switch (result.kind) {
      case 'created':
        setTitle('');
        setConcept('');
        setErrors({});
        onCreated(result.song);
        return;
      case 'invalid':
        setErrors({
          title: result.errors.title?.join(' '),
          concept: result.errors.concept?.join(' '),
        });
        if (result.errors.title === undefined && result.errors.concept === undefined) {
          setFailed(true);
        }
        return;
      case 'failed':
        setErrors({});
        setFailed(true);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={close}
      title="New Song"
      centered
      size="lg"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <form
        noValidate
        aria-label="New Song"
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="md">
          <TextInput
            label="Title"
            description={`One line, up to ${TITLE_MAXIMUM_LENGTH.toLocaleString('en-US')} characters. Titles do not have to be unique.`}
            required
            value={title}
            onChange={(event) => {
              setTitle(singleLine(event.currentTarget.value));
            }}
            onPaste={pasteTitle}
            error={errors.title}
            aria-invalid={errors.title !== undefined}
            data-autofocus
          />
          <Textarea
            label="Concept"
            description={`Optional: what the Song is about, up to ${CONCEPT_MAXIMUM_LENGTH.toLocaleString('en-US')} characters.`}
            rows={5}
            resize="vertical"
            value={concept}
            onChange={(event) => {
              setConcept(event.currentTarget.value);
            }}
            error={errors.concept}
            aria-invalid={errors.concept !== undefined}
          />
          <div role="status">
            {failed && (
              <Notice title="Song not created">
                <Text>{FAILED_MESSAGE}</Text>
              </Notice>
            )}
          </div>
          <Group justify="end">
            <Button variant="default" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" loading={submitting}>
              Create Song
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
