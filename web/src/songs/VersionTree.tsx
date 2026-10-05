import { Badge, Group, Stack, Switch, Text, Title, UnstyledButton } from '@mantine/core';
import { Link } from 'react-router';
import type { Version } from '../api/versions';
import { nestVersions, type VersionNode } from './versionNesting';

/** One Version in the tree: a link that selects it, with its children nested under it. */
function Node({
  node,
  selectedId,
  linkTo,
  linkState,
}: {
  node: VersionNode;
  selectedId: string | undefined;
  linkTo: (version: Version) => string;
  linkState: unknown;
}) {
  const { version, children } = node;
  const selected = version.id === selectedId;
  return (
    <li>
      <UnstyledButton
        component={Link}
        to={linkTo(version)}
        state={linkState}
        aria-current={selected ? 'true' : undefined}
        data-version-number={version.number}
        data-archived={version.archived || undefined}
        px="xs"
        py={4}
        w="100%"
        style={{
          display: 'block',
          borderRadius: 'var(--mantine-radius-sm)',
          background: selected ? 'var(--mantine-color-default-hover)' : undefined,
          borderInlineStart: selected
            ? '3px solid var(--mantine-primary-color-filled)'
            : '3px solid transparent',
          color: version.archived ? 'var(--n8-color-secondary-text)' : undefined,
          fontStyle: version.archived ? 'italic' : undefined,
        }}
      >
        <Group gap={6} wrap="nowrap">
          <Text span fw={700} ff="monospace">
            {version.number}
          </Text>
          {version.name !== null && (
            <Text span truncate>
              {version.name}
            </Text>
          )}
          {version.current && (
            <Badge size="sm" variant="filled" radius="sm" tt="none">
              Current
            </Badge>
          )}
          {version.archived && (
            <Text span size="xs">
              (archived)
            </Text>
          )}
        </Group>
      </UnstyledButton>
      {children.length > 0 && (
        <Branch nodes={children} selectedId={selectedId} linkTo={linkTo} linkState={linkState} />
      )}
    </li>
  );
}

function Branch({
  nodes,
  selectedId,
  linkTo,
  linkState,
  top = false,
}: {
  nodes: VersionNode[];
  selectedId: string | undefined;
  linkTo: (version: Version) => string;
  linkState: unknown;
  top?: boolean;
}) {
  return (
    <ul
      style={{
        listStyle: 'none',
        margin: 0,
        paddingInlineStart: top ? 0 : 'var(--mantine-spacing-md)',
      }}
    >
      {nodes.map((node) => (
        <Node
          key={node.version.id}
          node={node}
          selectedId={selectedId}
          linkTo={linkTo}
          linkState={linkState}
        />
      ))}
    </ul>
  );
}

/**
 * A Song's Versions as a tree drawn from their numbers: each under its nearest drawn ancestor
 * (`1.1` under `1`; a Version whose parent is missing goes under the nearest one that exists), all
 * expanded, siblings in numeric order. The current working Version is marked. Archived Versions are
 * drawn, dimmed, only while "Show archived" is on; the current one is always drawn. Selecting a
 * Version follows its link (`linkTo`), carrying `linkState` as the router state.
 */
export function VersionTree({
  versions,
  selectedId,
  showArchived,
  onShowArchived,
  linkTo,
  linkState,
}: {
  versions: Version[];
  selectedId: string | undefined;
  showArchived: boolean;
  onShowArchived: (show: boolean) => void;
  linkTo: (version: Version) => string;
  linkState?: unknown;
}) {
  const roots = nestVersions(
    versions,
    (version) => showArchived || !version.archived || version.current,
  );
  return (
    <Stack gap="xs" component="section" aria-labelledby="versions-heading">
      <Title order={3} size="h5" id="versions-heading">
        Versions
      </Title>
      <Switch
        size="sm"
        label="Show archived"
        checked={showArchived}
        onChange={(event) => {
          onShowArchived(event.currentTarget.checked);
        }}
      />
      <nav aria-label="Versions">
        <Branch nodes={roots} selectedId={selectedId} linkTo={linkTo} linkState={linkState} top />
      </nav>
    </Stack>
  );
}
