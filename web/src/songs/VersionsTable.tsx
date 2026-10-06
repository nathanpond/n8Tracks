import {
  ActionIcon,
  Anchor,
  Badge,
  Button,
  Group,
  Loader,
  Paper,
  Stack,
  Switch,
  Table,
  Text,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import { useState } from 'react';
import { Link } from 'react-router';
import {
  generationDuration,
  highestRating,
  ratingText,
  reportedModel,
  type Generation,
} from '../api/generations';
import type { LoadState } from '../api/songs';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { kindLabel, type Version } from '../api/versions';
import { ArtworkImage } from '../common/ArtworkImage';
import { ShortcodeBadge } from '../common/ShortcodeBadge';
import { Notice } from '../components/Notice';
import {
  GenerationActionsMenu,
  GenerationStateBadges,
  OpenInSuno,
  SunoCreated,
  type GenerationRowActions,
} from '../generations/GenerationParts';
import { StarRating } from '../generations/StarRating';
import { compareNumbers } from './versionNesting';
import { generationsByVersion, isGenerationListed, isListed } from './versionsTableRules';

/** The columns of a Version row: the expander, number, name, kind, status, count, rating, updated. */
const VERSION_COLUMNS = 8;

/** The Generation rows under an expanded Version: their own table, or why there are none to show. */
function GenerationRows({
  version,
  generations,
  showArchived,
  openId,
  linkTo,
  timeZone,
  onRate,
  actions,
}: {
  version: Version;
  generations: readonly Generation[];
  showArchived: boolean;
  openId: string | undefined;
  linkTo: (generation: Generation) => string;
  timeZone: string;
  onRate: (generation: Generation, rating: number | null) => void;
  actions: GenerationRowActions;
}) {
  if (generations.length === 0) {
    return (
      <Text size="sm" py="xs" data-testid="no-generations">
        No Generations yet
      </Text>
    );
  }
  const listed = generations.filter((generation) => isGenerationListed(generation, showArchived));
  if (listed.length === 0) {
    const count = generations.length;
    return (
      <Text size="sm" py="xs" data-testid="generations-hidden">
        {count === 1 ? '1 archived Generation is' : `${String(count)} archived Generations are`}{' '}
        hidden. Turn on Show archived Generations to list{count === 1 ? ' it' : ' them'}.
      </Text>
    );
  }
  return (
    <Table.ScrollContainer minWidth={960}>
      <Table
        withTableBorder
        aria-label={`Generations of Version ${version.number}`}
        data-generations-of={version.number}
      >
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">Shortcode</Table.Th>
            <Table.Th scope="col">Suno title</Table.Th>
            <Table.Th scope="col">Duration</Table.Th>
            <Table.Th scope="col">Model</Table.Th>
            <Table.Th scope="col">Rating</Table.Th>
            <Table.Th scope="col">Comments</Table.Th>
            <Table.Th scope="col">State</Table.Th>
            <Table.Th scope="col">Created in Suno</Table.Th>
            <Table.Th scope="col">Suno</Table.Th>
            <Table.Th scope="col">
              <VisuallyHidden>Actions</VisuallyHidden>
            </Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {listed.map((generation) => {
            const open = generation.id === openId;
            const title = generation.title ?? 'Untitled';
            return (
              <Table.Tr
                key={generation.id}
                data-generation={generation.shortcode}
                data-open={open || undefined}
                bg={open ? 'var(--mantine-color-default-hover)' : undefined}
              >
                <Table.Td style={{ whiteSpace: 'nowrap' }}>
                  <ShortcodeBadge shortcode={generation.shortcode} testId="generation-shortcode" />
                </Table.Td>
                <Table.Th scope="row" fw="normal">
                  <Group gap="xs" wrap="nowrap">
                    {generation.artwork !== null && (
                      <ArtworkImage
                        artwork={generation.artwork}
                        title={generation.shortcode}
                        size="96"
                        pixels={32}
                      />
                    )}
                    <Anchor
                      component={Link}
                      to={linkTo(generation)}
                      aria-label={`${title}, ${generation.shortcode}`}
                      aria-current={open ? 'true' : undefined}
                    >
                      {title}
                    </Anchor>
                  </Group>
                </Table.Th>
                <Table.Td style={{ whiteSpace: 'nowrap' }} data-testid="generation-duration">
                  {generationDuration(generation)}
                </Table.Td>
                <Table.Td>{reportedModel(generation) ?? 'Unknown'}</Table.Td>
                <Table.Td style={{ whiteSpace: 'nowrap' }} data-testid="generation-rating">
                  <StarRating
                    size="sm"
                    value={generation.rating}
                    label={`Rating of ${generation.shortcode}`}
                    onChange={(rating) => {
                      onRate(generation, rating);
                    }}
                  />
                </Table.Td>
                <Table.Td ta="end" data-testid="generation-comment-count">
                  {generation.comments.length}
                </Table.Td>
                <Table.Td>
                  <GenerationStateBadges generation={generation} />
                </Table.Td>
                <Table.Td style={{ whiteSpace: 'nowrap' }}>
                  <SunoCreated generation={generation} timeZone={timeZone} />
                </Table.Td>
                <Table.Td style={{ whiteSpace: 'nowrap' }}>
                  <OpenInSuno generation={generation} />
                </Table.Td>
                <Table.Td>
                  <GenerationActionsMenu generation={generation} actions={actions} />
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/**
 * The Song page's Versions and Generations section, below the editor and collapsible: a table of
 * the Song's Versions in tree order, one row each (number, name, kind, Current, Frozen, and
 * Archived badges, its count of Generations and highest rating over every state, and when it was
 * last updated), the selected Version's row marked. Archived Versions are listed only while
 * `showArchived` is on (the current one too); deleted Versions' placeholders never are. A
 * row's number opens that Version in the editor without making it current; its chevron button
 * shows its Generations beneath it in ordinal order, where an archived Generation is listed only
 * while `showArchivedGenerations` is on (the Selected one always is). Each Generation row has its
 * shortcode with a one-click copy, its star rating (set here too, `onRate`; the Version's highest
 * rating follows at once), its comment count, and an actions menu that archives or reactivates it
 * and selects it for the Song or clears that (`actions`); its title opens the Generation panel. `revealVersionId`
 * expands that Version's row (a link to one of its Generations); expansion is otherwise this page's.
 */
export function VersionsTable({
  versions,
  generations,
  onRetry,
  selectedId,
  openGenerationId,
  revealVersionId,
  showArchived,
  showArchivedGenerations,
  onShowArchived,
  onShowArchivedGenerations,
  versionLink,
  generationLink,
  onRate,
  actions,
}: {
  versions: readonly Version[];
  generations: LoadState<Generation[]>;
  onRetry: () => void;
  selectedId: string | undefined;
  openGenerationId: string | undefined;
  revealVersionId: string | undefined;
  showArchived: boolean;
  showArchivedGenerations: boolean;
  onShowArchived: (show: boolean) => void;
  onShowArchivedGenerations: (show: boolean) => void;
  versionLink: (version: Version) => string;
  generationLink: (generation: Generation) => string;
  onRate: (generation: Generation, rating: number | null) => void;
  actions: GenerationRowActions;
}) {
  const timeZone = useConfiguredTimeZone();
  const [open, setOpen] = useState(true);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() =>
    revealVersionId === undefined ? new Set() : new Set([revealVersionId]),
  );
  const [revealed, setRevealed] = useState(revealVersionId);

  // A link to a Generation shows it: its Version's row is expanded when the link names it.
  if (revealVersionId !== revealed) {
    setRevealed(revealVersionId);
    if (revealVersionId !== undefined && !expanded.has(revealVersionId)) {
      setExpanded(new Set([...expanded, revealVersionId]));
    }
  }

  const toggle = (id: string) => {
    const next = new Set(expanded);
    if (!next.delete(id)) {
      next.add(id);
    }
    setExpanded(next);
  };

  const rows = [...versions]
    .filter((version) => isListed(version, showArchived))
    .sort((left, right) => compareNumbers(left.number, right.number));
  const grouped =
    generations.phase === 'ready' ? generationsByVersion(generations.data) : undefined;

  return (
    <Paper p="md" withBorder component="section" aria-labelledby="versions-table-heading">
      <Stack gap="sm">
        <Group justify="space-between" wrap="wrap" gap="sm">
          <Title order={3} size="h4" id="versions-table-heading">
            Versions and Generations
          </Title>
          <Button
            variant="default"
            size="compact-sm"
            aria-expanded={open}
            aria-controls="versions-table-body"
            onClick={() => {
              setOpen(!open);
            }}
          >
            {open ? 'Hide the table' : 'Show the table'}
          </Button>
        </Group>
        {open && (
          <Stack gap="sm" id="versions-table-body">
            <Group gap="lg" wrap="wrap">
              <Switch
                size="sm"
                label="Show archived Versions"
                checked={showArchived}
                onChange={(event) => {
                  onShowArchived(event.currentTarget.checked);
                }}
              />
              <Switch
                size="sm"
                label="Show archived Generations"
                checked={showArchivedGenerations}
                onChange={(event) => {
                  onShowArchivedGenerations(event.currentTarget.checked);
                }}
              />
            </Group>
            {generations.phase === 'loading' && <Loader aria-label="Loading the Generations" />}
            {(generations.phase === 'error' || generations.phase === 'not-found') && (
              <Notice title="The Generations could not be loaded">
                <Text>
                  n8Tracks did not answer as expected. Check that it is running and try again.
                </Text>
                <div>
                  <Button variant="default" size="xs" onClick={onRetry}>
                    Try again
                  </Button>
                </div>
              </Notice>
            )}
            {grouped !== undefined && (
              <Table.ScrollContainer minWidth={760}>
                <Table withTableBorder aria-labelledby="versions-table-heading">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th scope="col">
                        <VisuallyHidden>Show Generations</VisuallyHidden>
                      </Table.Th>
                      <Table.Th scope="col">Version</Table.Th>
                      <Table.Th scope="col">Name</Table.Th>
                      <Table.Th scope="col">Kind</Table.Th>
                      <Table.Th scope="col">Status</Table.Th>
                      <Table.Th scope="col">Generations</Table.Th>
                      <Table.Th scope="col">Highest rating</Table.Th>
                      <Table.Th scope="col">Last updated</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {rows.map((version) => {
                      const own = grouped.get(version.id) ?? [];
                      const isExpanded = expanded.has(version.id);
                      const selected = version.id === selectedId;
                      const panelId = `version-generations-${version.id}`;
                      return [
                        <Table.Tr
                          key={version.id}
                          data-version-row={version.number}
                          data-selected={selected || undefined}
                          bg={selected ? 'var(--mantine-color-default-hover)' : undefined}
                        >
                          <Table.Td>
                            <ActionIcon
                              variant="subtle"
                              color="gray"
                              aria-expanded={isExpanded}
                              aria-controls={isExpanded ? panelId : undefined}
                              aria-label={`Generations of Version ${version.number}`}
                              onClick={() => {
                                toggle(version.id);
                              }}
                            >
                              <span aria-hidden="true">{isExpanded ? '▾' : '▸'}</span>
                            </ActionIcon>
                          </Table.Td>
                          <Table.Th scope="row" style={{ whiteSpace: 'nowrap' }}>
                            <Anchor
                              component={Link}
                              to={versionLink(version)}
                              ff="monospace"
                              fw={700}
                              aria-label={`Version ${version.number}`}
                              aria-current={selected ? 'true' : undefined}
                            >
                              {version.number}
                            </Anchor>
                          </Table.Th>
                          <Table.Td style={{ maxWidth: 240, overflowWrap: 'anywhere' }}>
                            {version.name}
                          </Table.Td>
                          <Table.Td>{kindLabel(version.kind)}</Table.Td>
                          <Table.Td>
                            <Group gap={4} wrap="wrap">
                              {version.current && (
                                <Badge size="sm" variant="filled" radius="sm" tt="none">
                                  Current
                                </Badge>
                              )}
                              {version.isFrozen && (
                                <Badge size="sm" variant="default" radius="sm" tt="none">
                                  Frozen
                                </Badge>
                              )}
                              {version.archived && (
                                <Badge size="sm" variant="default" radius="sm" tt="none">
                                  Archived
                                </Badge>
                              )}
                            </Group>
                          </Table.Td>
                          <Table.Td ta="end" data-testid="generation-count">
                            {own.length}
                          </Table.Td>
                          <Table.Td style={{ whiteSpace: 'nowrap' }} data-testid="highest-rating">
                            {ratingText(highestRating(own))}
                          </Table.Td>
                          <Table.Td style={{ whiteSpace: 'nowrap' }}>
                            <time dateTime={version.updatedAt}>
                              {formatDateTime(version.updatedAt, timeZone)}
                            </time>
                          </Table.Td>
                        </Table.Tr>,
                        isExpanded && (
                          <Table.Tr key={`${version.id}-generations`} id={panelId}>
                            <Table.Td colSpan={VERSION_COLUMNS} ps="xl">
                              <GenerationRows
                                version={version}
                                generations={own}
                                showArchived={showArchivedGenerations}
                                openId={openGenerationId}
                                linkTo={generationLink}
                                timeZone={timeZone}
                                onRate={onRate}
                                actions={actions}
                              />
                            </Table.Td>
                          </Table.Tr>
                        ),
                      ];
                    })}
                  </Table.Tbody>
                </Table>
              </Table.ScrollContainer>
            )}
          </Stack>
        )}
      </Stack>
    </Paper>
  );
}
