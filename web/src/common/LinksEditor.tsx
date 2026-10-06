import { Anchor, Button, Fieldset, Group, Stack, Text, TextInput } from '@mantine/core';
import { useState } from 'react';
import { linkErrors, normaliseArtistName } from '../artists/artistRules';
import { move } from './listMove';
import { RowControls } from './RowControls';
import type { Save } from './SavedTextField';
import { saveError } from './useInPlaceEdit';

/** An external link as a record holds it: its label (null for none) and an http or https URL. */
export interface EditedLink {
  label: string | null;
  url: string;
}

/** What the links editor holds: the text as typed. */
type LinkDrafts = { label: string; url: string }[];

const linkDraftsOf = (links: readonly EditedLink[]): LinkDrafts =>
  links.map((link) => ({ label: link.label ?? '', url: link.url }));

function normaliseLinks(links: LinkDrafts): EditedLink[] {
  return links.map((link) => {
    const label = normaliseArtistName(link.label);
    return { label: label === '' ? null : label, url: link.url.trim() };
  });
}

/**
 * A record's external links (an Album's, a Song's): edited as a list (add, reorder, remove) and
 * saved together with "Save links" as JSON text under `field`, the key the record's shared save
 * helper and the API's errors use. Each link is checked by the Artist link rules first. Mount it
 * afresh (by key) each time the record's links change, so it starts from what is stored.
 */
export function LinksEditor({
  links,
  field,
  maximum,
  empty,
  save,
}: {
  links: readonly EditedLink[];
  field: string;
  maximum: number;
  /** Shown while there are no links. */
  empty: string;
  save: Save;
}) {
  const [drafts, setDrafts] = useState(() => linkDraftsOf(links));
  const [errors, setErrors] = useState<{ label?: string; url?: string }[]>([]);
  const [listError, setListError] = useState<string>();
  const [saving, setSaving] = useState(false);
  const value = JSON.stringify(normaliseLinks(drafts));
  const dirty = value !== JSON.stringify(links);

  const change = (next: LinkDrafts) => {
    setDrafts(next);
    setListError(undefined);
  };

  const submit = async () => {
    const local = drafts.map((link) => linkErrors({ label: link.label, url: link.url }));
    if (local.some((link) => link.label !== undefined || link.url !== undefined)) {
      setErrors(local);
      return;
    }
    setErrors([]);
    setSaving(true);
    const outcome = await save(field, value);
    setSaving(false);
    setListError(saveError(outcome, field));
  };

  return (
    <Fieldset legend="Links">
      <Stack gap="xs">
        {drafts.length === 0 && (
          <Text size="sm" c="var(--n8-color-secondary-text)">
            {empty}
          </Text>
        )}
        {drafts.map((link, index) => {
          const position = String(index + 1);
          const update = (next: Partial<LinkDrafts[number]>) => {
            change(drafts.map((item, i) => (i === index ? { ...item, ...next } : item)));
          };
          return (
            <Group key={index} align="flex-start" wrap="wrap" gap="xs">
              <TextInput
                label={`Link ${position} label`}
                value={link.label}
                onChange={(event) => {
                  update({ label: event.currentTarget.value });
                }}
                error={errors[index]?.label}
                aria-invalid={errors[index]?.label !== undefined}
                w={180}
              />
              <TextInput
                label={`Link ${position} URL`}
                type="url"
                placeholder="https://"
                value={link.url}
                onChange={(event) => {
                  update({ url: event.currentTarget.value });
                }}
                error={errors[index]?.url}
                aria-invalid={errors[index]?.url !== undefined}
                style={{ flex: 1, minWidth: 220 }}
              />
              <Stack gap={0} pt={22}>
                <RowControls
                  noun="link"
                  index={index}
                  count={drafts.length}
                  onMove={(from, to) => {
                    change(move(drafts, from, to));
                  }}
                  onRemove={(i) => {
                    change(drafts.filter((_, j) => j !== i));
                  }}
                />
              </Stack>
            </Group>
          );
        })}
        {listError !== undefined && (
          <Text size="sm" c="var(--mantine-color-error)">
            {listError}
          </Text>
        )}
        {links.length > 0 && (
          <Stack gap={2}>
            <Text size="sm" fw={500}>
              Saved links
            </Text>
            {links.map((link, index) => (
              <Anchor
                key={index}
                href={link.url}
                target="_blank"
                rel="noopener noreferrer"
                size="sm"
                underline="always"
              >
                {link.label ?? link.url}
              </Anchor>
            ))}
          </Stack>
        )}
        <Group>
          <Button
            variant="default"
            size="xs"
            disabled={drafts.length >= maximum}
            onClick={() => {
              change([...drafts, { label: '', url: '' }]);
            }}
          >
            Add link
          </Button>
          <Button
            size="xs"
            disabled={!dirty}
            loading={saving}
            onClick={() => {
              void submit();
            }}
          >
            Save links
          </Button>
          <Button
            variant="default"
            size="xs"
            disabled={!dirty}
            onClick={() => {
              setDrafts(linkDraftsOf(links));
              setErrors([]);
              setListError(undefined);
            }}
          >
            Discard link changes
          </Button>
        </Group>
      </Stack>
    </Fieldset>
  );
}
