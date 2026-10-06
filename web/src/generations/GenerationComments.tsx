import { Badge, Button, Group, Paper, Stack, Text, Textarea, Title } from '@mantine/core';
import { useEffect, useId, useRef, useState, type SyntheticEvent, type RefObject } from 'react';
import {
  addComment,
  deleteComment,
  editComment,
  type Generation,
  type GenerationComment,
} from '../api/generations';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { canSaveComment, commentLength } from './evaluationRules';

/** Changes one Generation in the Song's cached list (see `useSongGenerations`). */
export type UpdateGeneration = (id: string, change: (generation: Generation) => Generation) => void;

const FAILED =
  'The comment could not be saved: n8Tracks did not answer as expected. Check that it is running and try again.';

/** The API's reasons for refusing a comment, as one message. */
function invalidMessage(errors: Record<string, string[]>): string {
  const message = Object.values(errors).flat().join(' ');
  return message === '' ? FAILED : message;
}

function replaceComment(comment: GenerationComment) {
  return (generation: Generation): Generation => ({
    ...generation,
    comments: generation.comments.map((other) => (other.id === comment.id ? comment : other)),
  });
}

function removeComment(id: string) {
  return (generation: Generation): Generation => ({
    ...generation,
    comments: generation.comments.filter((comment) => comment.id !== id),
  });
}

/** A text box for a comment with its counter: refuses too long text by saying so, never by cutting it. */
function CommentBox({
  label,
  value,
  onChange,
  inputRef,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  inputRef?: RefObject<HTMLTextAreaElement | null>;
}) {
  const { counter, error } = commentLength(value);
  return (
    <Textarea
      label={label}
      description={counter}
      rows={3}
      resize="vertical"
      value={value}
      ref={inputRef}
      onChange={(event) => {
        onChange(event.currentTarget.value);
      }}
      error={error}
      aria-invalid={error !== undefined}
    />
  );
}

/** One comment: its text, when it was written and whether it was edited, and its edit and delete controls. */
function CommentItem({
  generation,
  comment,
  position,
  update,
  onProblem,
}: {
  generation: Generation;
  comment: GenerationComment;
  position: number;
  update: UpdateGeneration;
  onProblem: (problem: string | undefined) => void;
}) {
  const timeZone = useConfiguredTimeZone();
  const [mode, setMode] = useState<'view' | 'edit' | 'confirm-delete'>('view');
  const [draft, setDraft] = useState(comment.text);
  const [busy, setBusy] = useState(false);
  const keep = useRef<HTMLButtonElement>(null);
  const box = useRef<HTMLTextAreaElement>(null);
  const editButton = useRef<HTMLButtonElement>(null);
  const restoreFocus = useRef(false);
  const name = `comment ${String(position)}`;

  // Focus goes into the edit box or onto "Keep it", and back to Edit when either closes.
  useEffect(() => {
    if (mode === 'confirm-delete') {
      keep.current?.focus();
    } else if (mode === 'edit') {
      box.current?.focus();
    } else if (restoreFocus.current) {
      restoreFocus.current = false;
      editButton.current?.focus();
    }
  }, [mode]);

  const backToView = () => {
    restoreFocus.current = true;
    setMode('view');
  };

  const save = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!canSaveComment(draft)) {
      return;
    }
    setBusy(true);
    const result = await editComment(generation.id, comment, draft);
    setBusy(false);
    if (result.kind === 'saved') {
      update(generation.id, replaceComment(result.record));
      onProblem(undefined);
      backToView();
    } else if (result.kind === 'conflict') {
      update(generation.id, replaceComment(result.current));
      setDraft(result.current.text);
      onProblem(
        'This comment was changed somewhere else since you opened it, so your edit was not saved. It now shows the saved text; edit it again if you still want to.',
      );
      backToView();
    } else if (result.kind === 'failed' && result.reason === 'gone') {
      update(generation.id, removeComment(comment.id));
      onProblem(undefined);
    } else if (result.kind === 'invalid') {
      onProblem(invalidMessage(result.errors));
    } else {
      onProblem(FAILED);
    }
  };

  const remove = async () => {
    setBusy(true);
    const result = await deleteComment(generation.id, comment);
    setBusy(false);
    if (result.kind === 'deleted' || result.kind === 'gone') {
      update(generation.id, removeComment(comment.id));
      onProblem(undefined);
    } else if (result.kind === 'conflict') {
      update(generation.id, replaceComment(result.current));
      onProblem(
        'This comment was changed somewhere else, so it was not deleted. Check its text, then delete it again if you still want to.',
      );
      backToView();
    } else {
      onProblem(
        'The comment could not be deleted: n8Tracks did not answer as expected. Check that it is running and try again.',
      );
    }
  };

  return (
    <Paper component="li" withBorder p="sm" data-comment={comment.id}>
      {mode === 'edit' ? (
        <form
          onSubmit={(event) => {
            void save(event);
          }}
        >
          <Stack gap="xs">
            <CommentBox label={`Edit ${name}`} value={draft} onChange={setDraft} inputRef={box} />
            <Group gap="xs">
              <Button type="submit" size="xs" loading={busy} disabled={!canSaveComment(draft)}>
                Save
              </Button>
              <Button
                variant="default"
                size="xs"
                onClick={() => {
                  setDraft(comment.text);
                  backToView();
                }}
              >
                Cancel
              </Button>
            </Group>
          </Stack>
        </form>
      ) : (
        <Stack gap={6}>
          <Text size="sm" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
            {comment.text}
          </Text>
          <Group gap="xs" wrap="wrap">
            <Text size="xs" c="var(--n8-color-secondary-text)">
              Written{' '}
              <time dateTime={comment.createdAt}>
                {formatDateTime(comment.createdAt, timeZone)}
              </time>
            </Text>
            {comment.editedAt !== null && (
              <Badge size="xs" variant="default" radius="sm" tt="none" data-testid="comment-edited">
                Edited{' '}
                <time dateTime={comment.editedAt}>
                  {formatDateTime(comment.editedAt, timeZone)}
                </time>
              </Badge>
            )}
          </Group>
          {mode === 'confirm-delete' ? (
            <Stack gap="xs" role="group" aria-label={`Delete ${name}?`}>
              <Text size="sm">Delete this comment? It cannot be brought back.</Text>
              <Group gap="xs">
                <Button
                  color="red"
                  size="xs"
                  loading={busy}
                  onClick={() => {
                    void remove();
                  }}
                >
                  Delete {name}
                </Button>
                <Button ref={keep} variant="default" size="xs" onClick={backToView}>
                  Keep it
                </Button>
              </Group>
            </Stack>
          ) : (
            <Group gap="xs">
              <Button
                ref={editButton}
                variant="subtle"
                size="compact-xs"
                aria-label={`Edit ${name}`}
                onClick={() => {
                  setDraft(comment.text);
                  setMode('edit');
                }}
              >
                Edit
              </Button>
              <Button
                variant="subtle"
                color="red"
                size="compact-xs"
                aria-label={`Delete ${name}`}
                onClick={() => {
                  setMode('confirm-delete');
                }}
              >
                Delete
              </Button>
            </Group>
          )}
        </Stack>
      )}
    </Paper>
  );
}

/**
 * A Generation's comments, oldest first, each with when it was written and whether it was edited,
 * and a box to add one. Comments are plain text of up to 2,000 characters
 * (a counter shows how many; longer text is refused, never cut). Editing one is in place; deleting
 * one asks first. A comment deleted elsewhere just leaves the list. Each write changes the Song's
 * one cached copy of the Generation (`update`).
 */
export function GenerationComments({
  generation,
  update,
}: {
  generation: Generation;
  update: UpdateGeneration;
}) {
  const headingId = useId();
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();

  const add = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!canSaveComment(draft)) {
      return;
    }
    setBusy(true);
    const result = await addComment(generation.id, draft);
    setBusy(false);
    if (result.kind === 'saved') {
      const comment = result.record;
      update(generation.id, (current) => ({
        ...current,
        comments: [...current.comments, comment],
      }));
      setDraft('');
      setProblem(undefined);
    } else if (result.kind === 'invalid') {
      setProblem(invalidMessage(result.errors));
    } else {
      setProblem(
        result.reason === 'gone'
          ? 'The comment could not be saved: this Generation is no longer in n8Tracks.'
          : FAILED,
      );
    }
  };

  return (
    <Stack component="section" gap="sm" aria-labelledby={headingId}>
      <Title order={3} size="h5" id={headingId}>
        Comments
      </Title>
      {problem !== undefined && (
        <Text role="alert" size="sm" c="var(--mantine-color-error)">
          {problem}
        </Text>
      )}
      {generation.comments.length === 0 ? (
        <Text size="sm" data-testid="no-comments">
          No comments yet
        </Text>
      ) : (
        <Stack component="ol" gap="xs" m={0} p={0} style={{ listStyle: 'none' }}>
          {generation.comments.map((comment, index) => (
            <CommentItem
              key={comment.id}
              generation={generation}
              comment={comment}
              position={index + 1}
              update={update}
              onProblem={setProblem}
            />
          ))}
        </Stack>
      )}
      <form
        onSubmit={(event) => {
          void add(event);
        }}
      >
        <Stack gap="xs">
          <CommentBox label="New comment" value={draft} onChange={setDraft} />
          <div>
            <Button type="submit" size="xs" loading={busy} disabled={!canSaveComment(draft)}>
              Add comment
            </Button>
          </div>
        </Stack>
      </form>
    </Stack>
  );
}
