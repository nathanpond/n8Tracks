import { Button, Group, Loader, NativeSelect, Stack, Text, Title } from '@mantine/core';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import {
  fieldOf,
  useCreateFields,
  type CreateField,
  type CreateFields,
  type OptionValue,
} from '../api/createFields';
import {
  readVersionDefaults,
  saveVersionDefaults,
  useVersionDefaults,
  type VersionDefaults,
} from '../api/versionDefaults';
import { Notice } from '../components/Notice';
import {
  ChoiceControl,
  ModelControl,
  NumberControl,
  RangeControl,
  ToggleControl,
} from '../songs/inputs/OptionControls';
import { choiceLabel } from '../songs/inputs/optionFormat';

// The defaults form is generated from what the API serves: which options take a default (`keys`,
// decided from Suno's field inventory) and each option's field (`GET /api/v1/suno/create-fields`),
// so an option the inventory adds appears here without new code. Only the three form choosers,
// which have no field of their own, and the two conditions Suno's form shows a field under are
// spelled out here.

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const CONFLICT_MESSAGE =
  'The defaults were changed somewhere else, so they have been reloaded. Your changes were not saved: make them again.';

/** The three options that choose a form, which the inventory has no field for: their label, values, and the tab they belong to. */
const CHOOSERS: Readonly<
  Record<string, { label: string; values: string[]; tab: string; suno: string }>
> = {
  kind: { label: 'Kind', values: ['song', 'speech', 'sound'], tab: 'all', suno: 'song' },
  songMode: { label: 'Song mode', values: ['simple', 'advanced'], tab: 'songs', suno: 'advanced' },
  speechMode: {
    label: 'Speech mode',
    values: ['simple', 'advanced'],
    tab: 'speech',
    suno: 'advanced',
  },
};

/** The options naming a Suno model, picked from the models offered. */
const MODEL_OPTIONS = ['model', 'soundsModel'];

/** A conditional option is shown only when the other values meet Suno's condition for it. */
const SHOWN_WHEN: Readonly<Record<string, (value: (key: string) => OptionValue) => boolean>> = {
  durationSeconds: (value) => value('durationMode') === 'custom',
  soundScale: (value) => value('soundKey') !== 'any',
};

const SECTIONS = [
  { tab: 'all', title: 'Every new Song' },
  { tab: 'songs', title: 'Song' },
  { tab: 'speech', title: 'Speech' },
  { tab: 'sounds', title: 'Sound' },
];

/** A choice's value as the page writes it: `one_shot` is "One shot". */
const valueLabel = (value: string) => choiceLabel(value.replace(/_/g, ' '));

/** A field for a form chooser, so it is drawn by the same control as the inventory's choices. */
function chooserField(key: string): CreateField | undefined {
  const chooser = CHOOSERS[key];
  return (
    chooser && {
      key,
      label: chooser.label,
      tab: chooser.tab,
      modes: [],
      type: 'choice',
      values: chooser.values,
      option: key,
      help: null,
    }
  );
}

const same = (a: Record<string, OptionValue>, b: Record<string, OptionValue>) =>
  Object.keys(a).length === Object.keys(b).length &&
  Object.entries(a).every(([key, value]) => key in b && b[key] === value);

/** One option's control, drawn by its field's type. */
function OptionControl({
  optionKey,
  field,
  value,
  models,
  onChange,
}: {
  optionKey: string;
  field: CreateField;
  value: OptionValue;
  models: readonly string[];
  onChange: (value: OptionValue) => void;
}) {
  if (optionKey in CHOOSERS) {
    return (
      <ChoiceControl
        field={field}
        value={typeof value === 'string' ? value : null}
        onChange={(chosen) => {
          if (chosen !== null) {
            onChange(chosen);
          }
        }}
        readOnly={false}
      />
    );
  }
  if (MODEL_OPTIONS.includes(optionKey)) {
    return (
      <ModelControl
        field={field}
        models={models}
        value={typeof value === 'string' ? value : null}
        onChange={onChange}
        readOnly={false}
      />
    );
  }
  switch (field.type) {
    case 'toggle':
      return (
        <ToggleControl field={field} value={value === true} onChange={onChange} readOnly={false} />
      );
    case 'range':
      return (
        <RangeControl
          field={field}
          value={typeof value === 'number' ? value : (field.min ?? 0)}
          onChange={onChange}
          readOnly={false}
        />
      );
    case 'number':
      return (
        <NumberControl
          field={field}
          value={typeof value === 'number' ? value : null}
          onChange={onChange}
          readOnly={false}
          emptyLabel="Auto"
        />
      );
    default:
      return (
        <NativeSelect
          label={field.label}
          description={field.help ?? undefined}
          data={[
            ...(field.default === null ? [{ value: '', label: 'None' }] : []),
            ...(field.values ?? []).map((choice) => ({ value: choice, label: valueLabel(choice) })),
          ]}
          value={typeof value === 'string' ? value : ''}
          onChange={(event) => {
            const chosen = event.currentTarget.value;
            onChange(chosen === '' ? null : chosen);
          }}
          data-option={optionKey}
        />
      );
  }
}

/** One option: its control (the user's default, or Suno's when there is none), whose it is, and any notice or error. */
function DefaultRow({
  optionKey,
  field,
  value,
  isSet,
  ignored,
  error,
  models,
  onChange,
  onReset,
}: {
  optionKey: string;
  field: CreateField;
  value: OptionValue;
  isSet: boolean;
  ignored: string | undefined;
  error: string | undefined;
  models: readonly string[];
  onChange: (value: OptionValue) => void;
  onReset: () => void;
}) {
  return (
    <Stack gap={4} data-default={optionKey}>
      <OptionControl
        optionKey={optionKey}
        field={field}
        value={value}
        models={models}
        onChange={onChange}
      />
      <Group gap="xs">
        <Text size="xs" c="var(--n8-color-secondary-text)">
          {isSet ? 'Your default' : "Suno's default"}
        </Text>
        {isSet && (
          <Button
            variant="subtle"
            size="compact-xs"
            onClick={onReset}
            aria-label={`Use Suno's default for ${field.label}`}
          >
            Use Suno&apos;s default
          </Button>
        )}
      </Group>
      {ignored !== undefined && (
        <div data-testid={`ignored-${optionKey}`}>
          <Notice title="Not used for new Songs">
            <Text size="sm">{ignored}</Text>
          </Notice>
        </div>
      )}
      {error !== undefined && (
        <Text size="sm" c="var(--n8-notice-text)">
          {error}
        </Text>
      )}
    </Stack>
  );
}

/** The form once the defaults and the fields have loaded: the drafts are the section's own until saved. */
function DefaultsEditor({
  initial,
  fields,
  modelsChanged,
}: {
  initial: VersionDefaults;
  fields: CreateFields;
  modelsChanged: number;
}) {
  const [saved, setSaved] = useState(initial);
  const [drafts, setDrafts] = useState<Record<string, OptionValue>>(initial.defaults);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ text: string; problem: boolean } | undefined>();
  const [errors, setErrors] = useState<Record<string, string>>({});
  const seen = useRef(modelsChanged);

  // After the model list changes, which models are offered and which defaults are ignored are read again; the drafts stay.
  useEffect(() => {
    if (seen.current === modelsChanged) {
      return;
    }
    seen.current = modelsChanged;
    let current = true;
    void readVersionDefaults().then((fresh) => {
      if (current && fresh) {
        setSaved((previous) => ({ ...previous, ignored: fresh.ignored, models: fresh.models }));
      }
    });
    return () => {
      current = false;
    };
  }, [modelsChanged]);

  const fieldFor = (key: string) => chooserField(key) ?? fieldOf(fields, key);
  const sunoDefault = (key: string): OptionValue => {
    const chooser = CHOOSERS[key];
    if (chooser) {
      return chooser.suno;
    }
    if (MODEL_OPTIONS.includes(key)) {
      return saved.models[0] ?? null;
    }
    const field = fieldOf(fields, key);
    return field?.default ?? (field?.type === 'toggle' ? false : null);
  };
  const value = (key: string): OptionValue =>
    key in drafts ? (drafts[key] ?? null) : sunoDefault(key);
  const dirty = !same(drafts, saved.defaults);

  const set = (key: string, next: OptionValue) => {
    setDrafts((previous) => ({ ...previous, [key]: next }));
    setMessage(undefined);
  };
  const reset = (key: string) => {
    setDrafts((previous) =>
      Object.fromEntries(Object.entries(previous).filter(([k]) => k !== key)),
    );
    setMessage(undefined);
  };

  const save = async () => {
    setBusy(true);
    const result = await saveVersionDefaults(saved.revision, drafts);
    setBusy(false);
    switch (result.kind) {
      case 'saved':
        setSaved(result.record);
        setDrafts(result.record.defaults);
        setErrors({});
        setMessage({ text: 'Defaults saved. New Songs start with them.', problem: false });
        break;
      case 'conflict':
        setSaved(result.current);
        setDrafts(result.current.defaults);
        setErrors({});
        setMessage({ text: CONFLICT_MESSAGE, problem: true });
        break;
      case 'invalid':
        setErrors(
          Object.fromEntries(
            Object.entries(result.errors).map(([key, messages]) => [
              key.replace(/^defaults\./, ''),
              messages.join(' '),
            ]),
          ),
        );
        setMessage({ text: 'Some defaults were not accepted: see each one.', problem: true });
        break;
      default:
        setMessage({ text: FAILED_MESSAGE, problem: true });
        break;
    }
  };

  const rows = (tab: string): ReactNode[] =>
    saved.keys.flatMap((key) => {
      const field = fieldFor(key);
      const shown = SHOWN_WHEN[key];
      if (field?.tab !== tab || (shown !== undefined && !shown(value))) {
        return [];
      }
      return [
        <DefaultRow
          key={key}
          optionKey={key}
          field={field}
          value={value(key)}
          isSet={key in drafts}
          ignored={
            key in drafts && drafts[key] === saved.defaults[key] ? saved.ignored[key] : undefined
          }
          error={errors[key]}
          models={saved.models}
          onChange={(next) => {
            set(key, next);
          }}
          onReset={() => {
            reset(key);
          }}
        />,
      ];
    });

  return (
    <Stack gap="lg">
      {SECTIONS.map(({ tab, title }) => {
        const shown = rows(tab);
        return (
          shown.length > 0 && (
            <Stack key={tab} gap="sm" component="section" aria-labelledby={`defaults-${tab}`}>
              <Title order={4} id={`defaults-${tab}`}>
                {title}
              </Title>
              {shown}
            </Stack>
          )
        );
      })}
      <Group gap="sm">
        <Button
          onClick={() => {
            void save();
          }}
          disabled={!dirty || busy}
          loading={busy}
        >
          Save defaults
        </Button>
      </Group>
      <Group gap="xs" role="status" aria-live="polite" mih={28}>
        {message &&
          (message.problem ? (
            <Notice title="Not saved">
              <Text>{message.text}</Text>
            </Notice>
          ) : (
            <Text>{message.text}</Text>
          ))}
      </Group>
    </Stack>
  );
}

/**
 * The user's defaults for new Songs: what each new Song's Version 1 starts with, for every kind.
 * An option left at Suno's default follows Suno; a default no longer valid (a retired model) is
 * kept and flagged, and new Songs use Suno's until it is valid again. `modelsChanged` changes each
 * time the model list does.
 */
export function SunoDefaultsSection({ modelsChanged = 0 }: { modelsChanged?: number }) {
  const defaults = useVersionDefaults();
  const fields = useCreateFields();
  const failed = [defaults.state.phase, fields.state.phase].some(
    (phase) => phase === 'error' || phase === 'not-found',
  );

  return (
    <Stack gap="md" component="section" aria-labelledby="suno-defaults-heading">
      <Title order={3} id="suno-defaults-heading">
        Defaults for new Songs
      </Title>
      <Text>
        Each new Song&apos;s first Version starts with these options, whichever kind you switch it
        to. Options sent when a Song is created win, a Version created from another copies it, and
        changing a default never changes a Version that exists.
      </Text>
      {failed ? (
        <Notice title="The defaults could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button
              variant="default"
              size="xs"
              onClick={() => {
                defaults.reload();
                fields.reload();
              }}
            >
              Try again
            </Button>
          </div>
        </Notice>
      ) : defaults.state.phase === 'ready' && fields.state.phase === 'ready' ? (
        <DefaultsEditor
          initial={defaults.state.data}
          fields={fields.state.data}
          modelsChanged={modelsChanged}
        />
      ) : (
        <Loader aria-label="Loading defaults" />
      )}
    </Stack>
  );
}
