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
} from '@mantine/core';
import { useState, type ReactNode } from 'react';
import { Link, useNavigate } from 'react-router';
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
  type WithoutSelection,
  type WorkflowStateCounts,
} from '../api/dashboard';
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
};

/**
 * The dashboard (#228), the home page: Recently edited (the ten active Songs changed last, with See
 * all), By workflow state (each state's count, opening the Songs table filtered to it), and Without
 * a Selected Generation (how many active Songs with Generations have none selected, the ten changed
 * last, and the Songs table filtered the same way). Every count is the total of the Songs table the
 * link opens. All three come from one read, but each shows its own loading, empty, and failed state.
 * An instance with no Songs shows a welcome with New Song instead. The page reads again when the
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
        <Welcome />
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
        </SimpleGrid>
      )}
    </Stack>
  );
}
