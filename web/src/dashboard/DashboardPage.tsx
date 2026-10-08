import {
  Anchor,
  Button,
  Group,
  Loader,
  Paper,
  SimpleGrid,
  Stack,
  Text,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import { useState, type ReactNode } from 'react';
import { Link, useNavigate } from 'react-router';
import {
  dismissProblem,
  problemAddress,
  problemsAddress,
  reviewsAddress,
  unmatchedAddress,
} from '../api/attention';
import {
  isEmptyCatalog,
  recentlyEditedAddress,
  sectionData,
  stateAddress,
  useDashboard,
  withoutSelectionAddress,
  type Dashboard,
  type DashboardSectionKey,
  type DashboardSong,
  type DashboardStateCount,
  type RecentlyEdited,
  type SunoProblem,
  type SunoProblems,
  type SunoReviews,
  type UnmatchedFiles,
  type WithoutSelection,
  type WorkflowStateCounts,
} from '../api/dashboard';
import { importPath } from '../api/sunoImports';
import { problemText } from './attentionText';
import { useConfiguredTimeZone } from '../api/timeZone';
import { NewSongDialog } from '../songs/NewSongDialog';
import { RelativeTime, StateBadge } from '../songs/SongParts';

/** "1 Song", "2 Songs". */
function songCountText(count: number): string {
  return `${String(count)} ${count === 1 ? 'Song' : 'Songs'}`;
}

/** A section's frame: its heading, and its content, loader, or failure. */
function Section({
  id,
  title,
  action,
  children,
}: {
  id: DashboardSectionKey;
  title: string;
  action?: ReactNode;
  children: ReactNode;
}) {
  const headingId = `dashboard-${id}`;
  return (
    <Paper
      component="section"
      aria-labelledby={headingId}
      p="md"
      withBorder
      data-testid={`dashboard-section-${id}`}
    >
      <Stack gap="sm">
        <Group justify="space-between" align="baseline">
          <Title order={3} id={headingId}>
            {title}
          </Title>
          {action}
        </Group>
        {children}
      </Stack>
    </Paper>
  );
}

/**
 * What a section shows: its content once read; a loader while a read is in progress and it has
 * nothing to show; otherwise a sentence and Retry, which reads the whole dashboard again.
 */
function SectionBody<T>({
  title,
  data,
  loading,
  onRetry,
  children,
}: {
  title: string;
  data: T | undefined;
  loading: boolean;
  onRetry: () => void;
  children: (data: T) => ReactNode;
}) {
  if (data !== undefined) {
    return children(data);
  }
  if (loading) {
    return <Loader size="sm" aria-label={`Loading ${title}`} />;
  }
  return (
    <Stack gap="xs" align="flex-start" data-testid="section-failed">
      <Text>{title} could not be loaded.</Text>
      <Button variant="default" size="xs" onClick={onRetry} aria-label={`Retry ${title}`}>
        Retry
      </Button>
    </Stack>
  );
}

/** A list of Songs: each its title (opening the Song), shortcode, workflow state, and when it changed. */
function SongRows({
  songs,
  timeZone,
  label,
}: {
  songs: DashboardSong[];
  timeZone: string;
  label: string;
}) {
  return (
    <Stack component="ul" gap="xs" m={0} p={0} style={{ listStyle: 'none' }} aria-label={label}>
      {songs.map((song) => (
        <li key={song.id} data-testid="dashboard-song" data-song={song.shortcode}>
          <Group gap="xs" wrap="wrap" justify="space-between">
            <Group gap="xs" wrap="wrap">
              <Anchor component={Link} to={`/songs/${song.shortcode}`}>
                {song.title}
              </Anchor>
              <Text size="sm" c="var(--n8-color-secondary-text)">
                {song.shortcode}
              </Text>
              <StateBadge name={song.state.name} colour={song.state.colour} />
            </Group>
            <Text size="sm">
              <RelativeTime utc={song.updatedAt} timeZone={timeZone} />
            </Text>
          </Group>
        </li>
      ))}
    </Stack>
  );
}

function RecentlyEditedContent({
  section,
  timeZone,
}: {
  section: RecentlyEdited;
  timeZone: string;
}) {
  if (section.songs.length === 0) {
    return <Text>No active Songs to show. Archived Songs are left out.</Text>;
  }
  return <SongRows songs={section.songs} timeZone={timeZone} label="Recently edited Songs" />;
}

function StateRow({ state }: { state: DashboardStateCount }) {
  return (
    <li data-testid="state-count" data-state={state.id}>
      <Group gap="xs" justify="space-between" wrap="nowrap">
        <StateBadge name={state.name} colour={state.colour} />
        <Anchor
          component={Link}
          to={stateAddress(state.id)}
          aria-label={`${songCountText(state.songCount)} in ${state.name}`}
        >
          {songCountText(state.songCount)}
        </Anchor>
      </Group>
    </li>
  );
}

function WorkflowStatesContent({ section }: { section: WorkflowStateCounts }) {
  if (section.states.length === 0) {
    return <Text>There are no workflow states to show.</Text>;
  }
  return (
    <Stack
      component="ul"
      gap="xs"
      m={0}
      p={0}
      style={{ listStyle: 'none' }}
      aria-label="Songs by workflow state"
    >
      {section.states.map((state) => (
        <StateRow key={state.id} state={state} />
      ))}
    </Stack>
  );
}

function WithoutSelectionContent({
  section,
  timeZone,
}: {
  section: WithoutSelection;
  timeZone: string;
}) {
  if (section.count === 0) {
    return <Text>Every active Song with Generations has a Selected Generation.</Text>;
  }
  return (
    <Stack gap="sm">
      <Text data-testid="without-selection-count">
        {section.count === 1
          ? '1 active Song has Generations but no Selected Generation.'
          : `${String(section.count)} active Songs have Generations but no Selected Generation.`}
      </Text>
      <SongRows
        songs={section.songs}
        timeZone={timeZone}
        label="Songs without a Selected Generation"
      />
    </Stack>
  );
}

/** "1 audio file", "2 audio files". */
function fileCountText(count: number): string {
  return `${String(count)} audio ${count === 1 ? 'file' : 'files'}`;
}

/** "1 record", "2 records". */
function recordsText(count: number): string {
  return `${String(count)} ${count === 1 ? 'record' : 'records'}`;
}

/** A list's "n more", leading to the area's own page. */
function More({
  shown,
  total,
  to,
  label,
}: {
  shown: number;
  total: number;
  to: string;
  label: string;
}) {
  if (total <= shown) {
    return null;
  }
  const more = total - shown;
  return (
    <Anchor component={Link} to={to} size="sm" data-testid="attention-more">
      {`${String(more)} more`}
      <VisuallyHidden>{` ${label}`}</VisuallyHidden>
    </Anchor>
  );
}

function UnmatchedFilesContent({ section }: { section: UnmatchedFiles }) {
  if (section.mediaUnavailable) {
    return (
      <Text data-testid="media-unavailable">
        The media folder is unavailable, so the unmatched audio files are not counted until it is
        back.{' '}
        <Anchor component={Link} to={unmatchedAddress()} underline="always">
          Open Unmatched Files
        </Anchor>
      </Text>
    );
  }
  if (section.count === 0) {
    return <Text>No audio files are waiting to be placed.</Text>;
  }
  return (
    <Text data-testid="unmatched-count">
      <Anchor
        component={Link}
        to={unmatchedAddress()}
        underline="always"
        aria-label={`${fileCountText(section.count)} unmatched: open Unmatched Files`}
      >
        {fileCountText(section.count)}
      </Anchor>{' '}
      {section.count === 1 ? 'is' : 'are'} not associated with a Song or a Generation.
    </Text>
  );
}

function SunoReviewsContent({ section, timeZone }: { section: SunoReviews; timeZone: string }) {
  const exports = section.exports ?? [];
  if (section.count === 0) {
    return <Text>No Suno import is waiting for review.</Text>;
  }
  return (
    <Stack gap="xs">
      <Stack
        component="ul"
        gap="xs"
        m={0}
        p={0}
        style={{ listStyle: 'none' }}
        aria-label="Suno imports waiting for review"
      >
        {exports.map((review) => (
          <li key={review.exportId} data-testid="suno-review" data-export={review.exportId}>
            <Stack gap={2}>
              <Group gap="xs" wrap="wrap">
                <Anchor component={Link} to={importPath(review.exportId)}>
                  Review the import
                </Anchor>
                <Text size="sm" c="var(--n8-color-secondary-text)">
                  arrived <RelativeTime utc={review.arrivedAt} timeZone={timeZone} />
                </Text>
              </Group>
              <Text size="sm" data-testid="suno-review-counts">
                {`${recordsText(review.recordCount)}; ${String(review.changedCount)} changed and ${String(review.conflictCount)} ${review.conflictCount === 1 ? 'Conflict' : 'Conflicts'} to resolve`}
              </Text>
            </Stack>
          </li>
        ))}
      </Stack>
      <More
        shown={exports.length}
        total={section.count}
        to={reviewsAddress()}
        label="Suno imports"
      />
    </Stack>
  );
}

/** The link to where a problem is resolved. */
function problemLinkText(problem: SunoProblem): string {
  switch (problem.kind) {
    case 'failedSync':
      return 'Open Suno import';
    case 'failedGenerate':
      return `Open ${problem.versionShortcode ?? 'the Version'}`;
    case 'unavailableWorkspace':
      return 'Open the workspace';
  }
}

function ProblemRow({
  problem,
  timeZone,
  onDismissed,
}: {
  problem: SunoProblem;
  timeZone: string;
  onDismissed: () => void;
}) {
  const [dismissing, setDismissing] = useState(false);
  const [failed, setFailed] = useState(false);
  const text = problemText(problem);
  return (
    <li data-testid="suno-problem" data-kind={problem.kind} data-subject={problem.subject}>
      <Stack gap={4}>
        <Text size="sm">{text}</Text>
        <Group gap="xs" wrap="wrap">
          {problem.occurredAt !== null && (
            <Text size="sm" c="var(--n8-color-secondary-text)">
              <RelativeTime utc={problem.occurredAt} timeZone={timeZone} />
            </Text>
          )}
          <Anchor component={Link} to={problemAddress(problem)} size="sm">
            {problemLinkText(problem)}
          </Anchor>
          {problem.dismissible && (
            <Button
              variant="subtle"
              size="compact-xs"
              loading={dismissing}
              aria-label={`Dismiss: ${text}`}
              onClick={() => {
                setDismissing(true);
                setFailed(false);
                void dismissProblem(problem).then((dismissed) => {
                  setDismissing(false);
                  if (dismissed) {
                    onDismissed();
                  } else {
                    setFailed(true);
                  }
                });
              }}
            >
              Dismiss
            </Button>
          )}
        </Group>
        {failed && (
          <Text size="sm" role="alert">
            It could not be dismissed. Try again.
          </Text>
        )}
      </Stack>
    </li>
  );
}

function SunoProblemsContent({
  section,
  timeZone,
  onDismissed,
}: {
  section: SunoProblems;
  timeZone: string;
  onDismissed: () => void;
}) {
  const problems = section.problems ?? [];
  if (section.count === 0) {
    return <Text>No Suno problems to report.</Text>;
  }
  return (
    <Stack gap="xs">
      <Stack
        component="ul"
        gap="sm"
        m={0}
        p={0}
        style={{ listStyle: 'none' }}
        aria-label="Suno problems"
      >
        {problems.map((problem) => (
          <ProblemRow
            key={`${problem.kind}:${problem.subject}`}
            problem={problem}
            timeZone={timeZone}
            onDismissed={onDismissed}
          />
        ))}
      </Stack>
      <More
        shown={problems.length}
        total={section.count}
        to={problemsAddress()}
        label="Suno workspaces that are unavailable"
      />
    </Stack>
  );
}

/** The welcome an instance with no Songs shows in place of the catalog sections. */
function Welcome() {
  const navigate = useNavigate();
  const [creating, setCreating] = useState(false);
  return (
    <Paper p="lg" withBorder data-testid="dashboard-welcome">
      <Stack gap="sm" align="flex-start">
        <Title order={3}>Welcome to n8Tracks</Title>
        <Text>
          There are no Songs yet. A Song needs only a title, and starts with a Version 1 to write
          in. Once there are Songs, this page shows what you changed last and where each Song
          stands.
        </Text>
        <Button
          onClick={() => {
            setCreating(true);
          }}
        >
          New Song
        </Button>
      </Stack>
      <NewSongDialog
        opened={creating}
        onClose={() => {
          setCreating(false);
        }}
        onCreated={(song) => {
          setCreating(false);
          void navigate(`/songs/${song.shortcode}`);
        }}
      />
    </Paper>
  );
}

const TITLES: Record<DashboardSectionKey, string> = {
  recentlyEdited: 'Recently edited',
  workflowStates: 'By workflow state',
  withoutSelection: 'Without a Selected Generation',
  unmatchedFiles: 'Unmatched Files',
  sunoReviews: 'Suno reviews',
  sunoProblems: 'Suno problems',
};

/**
 * What needs attention (#229): Unmatched Files, Suno reviews, and Suno problems. Each says so in one
 * line when it has nothing to report, and shows its own failure with Retry.
 */
function AttentionSections({
  data,
  loading,
  onRetry,
  timeZone,
}: {
  data: Dashboard | undefined;
  loading: boolean;
  onRetry: () => void;
  timeZone: string;
}) {
  const unmatched = data === undefined ? undefined : sectionData(data.unmatchedFiles);
  const reviews = data === undefined ? undefined : sectionData(data.sunoReviews);
  const problems = data === undefined ? undefined : sectionData(data.sunoProblems);
  const sectionProps = { loading, onRetry };
  return (
    <>
      <Section id="unmatchedFiles" title={TITLES.unmatchedFiles}>
        <SectionBody title={TITLES.unmatchedFiles} data={unmatched} {...sectionProps}>
          {(section) => <UnmatchedFilesContent section={section} />}
        </SectionBody>
      </Section>
      <Section id="sunoReviews" title={TITLES.sunoReviews}>
        <SectionBody title={TITLES.sunoReviews} data={reviews} {...sectionProps}>
          {(section) => <SunoReviewsContent section={section} timeZone={timeZone} />}
        </SectionBody>
      </Section>
      <Section id="sunoProblems" title={TITLES.sunoProblems}>
        <SectionBody title={TITLES.sunoProblems} data={problems} {...sectionProps}>
          {(section) => (
            <SunoProblemsContent section={section} timeZone={timeZone} onDismissed={onRetry} />
          )}
        </SectionBody>
      </Section>
    </>
  );
}

/**
 * The dashboard (#228), the home page: Recently edited (the ten active Songs changed last, with See
 * all), By workflow state (each state's count, opening the Songs table filtered to it), and Without
 * a Selected Generation (how many active Songs with Generations have none selected, the ten changed
 * last, and the Songs table filtered the same way). Every count is the total of the Songs table the
 * link opens. Then (#229) what needs attention: Unmatched Files, Suno reviews, and Suno problems,
 * each count the total of the page it links to. All come from one read, but each shows its own
 * loading, empty, and failed state. An instance with no Songs shows a welcome with New Song instead
 * of the catalog sections, above what needs attention. The page reads again when the
 * window regains focus (at most every 30 seconds), showing what it had meanwhile and a small notice
 * if that read fails. One column on a narrow screen, two on a wide one.
 */
export function DashboardPage() {
  const { state, reload } = useDashboard();
  const timeZone = useConfiguredTimeZone();
  const data: Dashboard | undefined = state.data;

  const recent = data === undefined ? undefined : sectionData(data.recentlyEdited);
  const counts = data === undefined ? undefined : sectionData(data.workflowStates);
  const without = data === undefined ? undefined : sectionData(data.withoutSelection);
  const sectionProps = { loading: state.loading, onRetry: reload };

  return (
    <Stack gap="lg">
      <Title order={2}>Dashboard</Title>
      {state.refreshFailed && (
        <Text size="sm" role="status" data-testid="dashboard-stale">
          The dashboard could not be refreshed, so it shows what was read before.
        </Text>
      )}
      {data !== undefined && isEmptyCatalog(data) ? (
        <>
          <Welcome />
          {/* #229: what needs attention can come before any Song (a first sync waiting for review). */}
          <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
            <AttentionSections data={data} timeZone={timeZone} {...sectionProps} />
          </SimpleGrid>
        </>
      ) : (
        <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
          <Section
            id="recentlyEdited"
            title={TITLES.recentlyEdited}
            action={
              <Anchor
                component={Link}
                to={recentlyEditedAddress()}
                size="sm"
                aria-label="See all active Songs, last updated first"
              >
                See all
              </Anchor>
            }
          >
            <SectionBody title={TITLES.recentlyEdited} data={recent} {...sectionProps}>
              {(section) => <RecentlyEditedContent section={section} timeZone={timeZone} />}
            </SectionBody>
          </Section>
          <Section id="workflowStates" title={TITLES.workflowStates}>
            <SectionBody title={TITLES.workflowStates} data={counts} {...sectionProps}>
              {(section) => <WorkflowStatesContent section={section} />}
            </SectionBody>
          </Section>
          <Section
            id="withoutSelection"
            title={TITLES.withoutSelection}
            action={
              (without?.count ?? 0) > 0 && (
                <Anchor
                  component={Link}
                  to={withoutSelectionAddress()}
                  size="sm"
                  aria-label="Show in Songs without a Selected Generation"
                >
                  Show in Songs
                </Anchor>
              )
            }
          >
            <SectionBody title={TITLES.withoutSelection} data={without} {...sectionProps}>
              {(section) => <WithoutSelectionContent section={section} timeZone={timeZone} />}
            </SectionBody>
          </Section>
          <AttentionSections data={data} timeZone={timeZone} {...sectionProps} />
        </SimpleGrid>
      )}
    </Stack>
  );
}
