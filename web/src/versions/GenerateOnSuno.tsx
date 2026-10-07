import { Button, Group, List, Loader, Paper, Stack, Text } from '@mantine/core';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { unavailableSourceText, requestStateLabel } from './generateRules';
import type { GenerateOnSunoController, Problem } from './useGenerateOnSuno';

/** The Generate on Suno action, for the Version header beside Create New Version From. */
export function GenerateOnSunoButton({ controller }: { controller: GenerateOnSunoController }) {
  return (
    <Button
      variant="default"
      loading={controller.busy}
      disabled={controller.request?.active === true}
      onClick={controller.start}
      aria-describedby={`generate-on-suno-${controller.versionId}`}
    >
      Generate on Suno
    </Button>
  );
}

/**
 * What Generate on Suno has to say below the header: why it could not start (the extension, a blocked
 * source, a failed hand-off), and the request's progress as the extension reports it, with Cancel.
 */
export function GenerateOnSunoStatus({ controller }: { controller: GenerateOnSunoController }) {
  const timeZone = useConfiguredTimeZone();
  const { problem, request } = controller;
  if (problem === null && request === null) {
    return <span id={`generate-on-suno-${controller.versionId}`} hidden />;
  }
  return (
    <Paper
      withBorder
      p="sm"
      id={`generate-on-suno-${controller.versionId}`}
      data-testid="generate-on-suno"
      aria-label="Generate on Suno"
      component="section"
    >
      <Stack gap="xs">
        {problem !== null && (
          <ProblemView
            problem={problem}
            timeZone={timeZone}
            onOpenOptions={controller.openOptions}
          />
        )}
        {request !== null && (
          <Stack gap={4} data-testid="generation-request">
            <Group gap="sm" wrap="wrap">
              {request.active && <Loader size="xs" aria-hidden="true" />}
              <Text
                role="status"
                fw={600}
                data-testid="generation-request-state"
                data-state={request.state}
              >
                Generate on Suno: {requestStateLabel(request)}
              </Text>
              {request.active && (
                <Button
                  size="compact-sm"
                  variant="default"
                  disabled={controller.busy}
                  onClick={controller.cancel}
                >
                  Cancel the request
                </Button>
              )}
            </Group>
            {request.message !== null && (
              <Text size="sm" data-testid="generation-request-message">
                {request.message}
              </Text>
            )}
            {request.state === 'pending' && (
              <Text size="sm" c="var(--n8-color-secondary-text)">
                Nothing has been generated. The extension takes it from here; the Suno steps follow.
              </Text>
            )}
            <Text size="xs" c="var(--n8-color-secondary-text)">
              Started {formatDateTime(request.createdAt, timeZone)}
            </Text>
          </Stack>
        )}
      </Stack>
    </Paper>
  );
}

function ProblemView({
  problem,
  timeZone,
  onOpenOptions,
}: {
  problem: Problem;
  timeZone: string;
  onOpenOptions: () => void;
}) {
  switch (problem.kind) {
    case 'extension':
      return (
        <Stack gap={4} data-testid="extension-problem">
          <Text fw={600} role="alert">
            {problem.problem.title}
          </Text>
          <Text size="sm">{problem.problem.advice}</Text>
          {problem.problem.canOpenOptions && (
            <Group>
              <Button size="compact-sm" variant="default" onClick={onOpenOptions}>
                Open the extension’s options
              </Button>
            </Group>
          )}
          <Text size="xs" c="var(--n8-color-secondary-text)">
            Nothing was sent to Suno and no request was made.
          </Text>
        </Stack>
      );
    case 'blocked':
      return (
        <Stack gap={4} data-testid="sources-blocked">
          <Text fw={600} role="alert">
            Generate on Suno is blocked: a source this Version needs cannot be used.
          </Text>
          <List size="sm">
            {problem.sources.map((source) => (
              <List.Item
                key={`${source.group}-${String(source.position)}`}
                data-testid="blocked-source"
              >
                {unavailableSourceText(source)}
              </List.Item>
            ))}
          </List>
          <Text size="xs" c="var(--n8-color-secondary-text)" data-testid="last-sync">
            {problem.lastSyncAt === null
              ? 'Suno’s Trash and missing clips are as n8Tracks knows them; no sync has been confirmed yet.'
              : `Suno’s Trash and missing clips are as of the last confirmed sync, ${formatDateTime(problem.lastSyncAt, timeZone)}.`}
          </Text>
        </Stack>
      );
    case 'handoff':
      return (
        <Text role="alert" data-testid="handoff-problem">
          The extension did not take the request: {problem.message}
        </Text>
      );
    case 'failed':
      return (
        <Text role="alert" data-testid="generate-failed">
          {problem.message}
        </Text>
      );
  }
}
