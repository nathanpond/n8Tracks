import { Anchor, Button, Group, List, Loader, Paper, Stack, Text } from '@mantine/core';
import { Link } from 'react-router';
import type { ObservedCreate, Verification } from '../api/generationRequests';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { unavailableSourceText, requestStateLabel } from './generateRules';
import { observedText, optionList, skipReasonText } from './observedRules';
import {
  countsText,
  entryDetail,
  entryLabel,
  modeLabel,
  needsAttention,
  OUTCOME_LABELS,
} from './verificationRules';
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
            {request.observed !== undefined && request.observed.length > 0 && (
              <ObservedView observed={request.observed} timeZone={timeZone} />
            )}
            {request.verification != null && (
              <VerificationView verification={request.verification} timeZone={timeZone} />
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

/**
 * What each Create the user clicked in Suno came to (#149): the Generations recorded and on which
 * Version, the options that differed when a new Version was made, the options taken from the Version
 * because neither Suno's answer nor the page's request gave them, and any clip skipped.
 */
function ObservedView({ observed, timeZone }: { observed: ObservedCreate[]; timeZone: string }) {
  return (
    <Stack
      gap={4}
      data-testid="observed-creates"
      component="section"
      aria-labelledby="observed-title"
    >
      <Text fw={600} id="observed-title">
        Your Creates in Suno
      </Text>
      <List size="sm" spacing={4}>
        {observed.map((create) => (
          <List.Item
            key={create.observedAt + create.generations.map((item) => item.id).join()}
            data-testid="observed-create"
            data-outcome={create.outcome}
          >
            <Text size="sm" span>
              {formatDateTime(create.observedAt, timeZone)}: {observedText(create)}
            </Text>
            {create.version !== null && create.outcome === 'branched' && (
              <Text size="sm" data-testid="observed-version">
                <Anchor component={Link} underline="always" to={`/go/${create.version.shortcode}`}>
                  Open Version {create.version.number}
                </Anchor>
              </Text>
            )}
            {create.differing.length > 0 && create.outcome === 'branched' && (
              <Text size="sm" data-testid="observed-differing">
                Options that differed: {optionList(create.differing)}.
              </Text>
            )}
            {create.assumed.length > 0 && (
              <Text size="xs" c="var(--n8-color-secondary-text)" data-testid="observed-assumed">
                Taken from the Version, because Suno did not say: {optionList(create.assumed)}.
                {create.requestRead ? '' : ' What the page sent could not be read.'}
              </Text>
            )}
            {create.skipped.length > 0 && (
              <List size="xs" data-testid="observed-skipped">
                {create.skipped.map((item) => (
                  <List.Item key={item.sunoId}>
                    Skipped clip {item.sunoId}: {skipReasonText(item.reason)}.
                  </List.Item>
                ))}
              </List>
            )}
          </List.Item>
        ))}
      </List>
    </Stack>
  );
}

/**
 * The extension's verification summary of Suno's filled Create form (#146): every entry of the
 * Version's mode with its outcome, and the reminder that the user reviews the form and clicks
 * Create in Suno. Lyrics, styles, and prompts are shown by length only: n8Tracks receives no text.
 */
function VerificationView({
  verification,
  timeZone,
}: {
  verification: Verification;
  timeZone: string;
}) {
  return (
    <Stack
      gap={4}
      data-testid="verification"
      component="section"
      aria-labelledby="verification-title"
    >
      <Text fw={600} id="verification-title">
        Verification of Suno’s form ({modeLabel(verification.mode)})
      </Text>
      <Text size="sm" data-testid="verification-review">
        {needsAttention(verification)
          ? 'Some entries need you before you generate: review them in Suno’s form, then click Create there.'
          : 'Every entry is as the Version says: review Suno’s form, then click Create there.'}
      </Text>
      <Text size="sm" c="var(--n8-color-secondary-text)" data-testid="verification-counts">
        {countsText(verification)}
      </Text>
      <List size="sm" spacing={2}>
        {verification.entries.map((entry) => {
          const detail = entryDetail(entry);
          return (
            <List.Item
              key={entry.key}
              data-testid="verification-entry"
              data-outcome={entry.outcome}
              data-key={entry.key}
            >
              {entryLabel(entry.key)}: <strong>{OUTCOME_LABELS[entry.outcome]}</strong>
              {detail === '' ? null : ` — ${detail}`}
            </List.Item>
          );
        })}
      </List>
      <Text size="xs" c="var(--n8-color-secondary-text)">
        Checked {formatDateTime(verification.checkedAt, timeZone)} (Suno adapter{' '}
        {verification.adapterVersion})
      </Text>
    </Stack>
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
