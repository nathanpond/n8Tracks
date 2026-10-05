import { MantineProvider } from '@mantine/core';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { fieldOf, type CreateField } from '../../api/createFields';
import { CREATE_FIELDS } from '../../test/createFieldsFixture';
import {
  ChoiceControl,
  ModelControl,
  RangeControl,
  StepsControl,
  TextControl,
  ToggleControl,
} from './OptionControls';
import { formatDuration, spokenDuration } from './optionFormat';

function field(option: string): CreateField {
  const found = fieldOf(CREATE_FIELDS, option);
  if (found === undefined) {
    throw new Error(`The fixture has no field for ${option}.`);
  }
  return found;
}

/** Renders a controlled control holding its own value; `changes` records every value it sent. */
function renderControl<T>(
  initial: T,
  draw: (props: { value: T; onChange: (value: T) => void }) => React.ReactNode,
) {
  const changes = vi.fn<(value: T) => void>();
  function Holder() {
    const [value, setValue] = useState(initial);
    return draw({
      value,
      onChange: (next) => {
        changes(next);
        setValue(next);
      },
    });
  }
  render(
    <MantineProvider>
      <Holder />
    </MantineProvider>,
  );
  return { user: userEvent.setup(), changes };
}

describe('a range control', () => {
  it('takes its range from the field, steps by keyboard, and announces the value in its unit', async () => {
    const { user, changes } = renderControl(80, ({ value, onChange }) => (
      <RangeControl field={field('weirdness')} value={value} onChange={onChange} readOnly={false} />
    ));

    const slider = screen.getByRole('slider', { name: 'Weirdness' });
    expect(slider).toHaveAttribute('aria-valuemin', '0');
    expect(slider).toHaveAttribute('aria-valuemax', '100');
    expect(slider).toHaveAttribute('aria-valuenow', '80');
    expect(slider).toHaveAttribute('aria-valuetext', '80 percent');
    expect(slider).toHaveAccessibleDescription('Turn it up for wild, unexpected results');
    expect(screen.getByText('Turn it up for wild, unexpected results')).toBeVisible();

    slider.focus();
    await user.keyboard('{ArrowRight}');
    expect(slider).toHaveAttribute('aria-valuenow', '81');
    expect(slider).toHaveAttribute('aria-valuetext', '81 percent');
    await user.keyboard('{ArrowLeft}{ArrowLeft}');
    expect(slider).toHaveAttribute('aria-valuenow', '79');

    // The range's ends are the field's: nothing goes past them.
    await user.keyboard('{End}{ArrowRight}');
    expect(slider).toHaveAttribute('aria-valuenow', '100');
    await user.keyboard('{Home}{ArrowLeft}');
    expect(slider).toHaveAttribute('aria-valuenow', '0');
    expect(changes.mock.calls.flat().every((value) => value >= 0 && value <= 100)).toBe(true);
    expect(changes).toHaveBeenLastCalledWith(0);
  });

  it('announces a custom duration as minutes and seconds, from 10 seconds to 6 minutes', async () => {
    const { user } = renderControl(120, ({ value, onChange }) => (
      <RangeControl
        field={field('durationSeconds')}
        label="Custom duration"
        value={value}
        onChange={onChange}
        readOnly={false}
      />
    ));

    const slider = screen.getByRole('slider', { name: 'Custom duration' });
    expect(slider).toHaveAttribute('aria-valuetext', '2 minutes');
    expect(screen.getAllByText('2:00')[0]).toBeVisible();
    slider.focus();
    await user.keyboard('{Home}');
    expect(slider).toHaveAttribute('aria-valuenow', '10');
    expect(slider).toHaveAttribute('aria-valuetext', '10 seconds');
    await user.keyboard('{End}');
    expect(slider).toHaveAttribute('aria-valuenow', '360');
    expect(slider).toHaveAttribute('aria-valuetext', '6 minutes');
  });

  it('cannot be moved when read only', async () => {
    const { user, changes } = renderControl(50, ({ value, onChange }) => (
      <RangeControl field={field('weirdness')} value={value} onChange={onChange} readOnly />
    ));

    const slider = screen.getByRole('slider', { name: 'Weirdness' });
    slider.focus();
    await user.keyboard('{ArrowRight}{End}');
    expect(slider).toHaveAttribute('aria-valuenow', '50');
    expect(changes).not.toHaveBeenCalled();
  });
});

describe('a steps control', () => {
  it('offers the field’s values in order, by keyboard, announcing each by name', async () => {
    const { user, changes } = renderControl('normal', ({ value, onChange }) => (
      <StepsControl field={field('variety')} value={value} onChange={onChange} readOnly={false} />
    ));

    const slider = screen.getByRole('slider', { name: 'Variety' });
    expect(slider).toHaveAttribute('aria-valuetext', 'Normal');
    expect(slider).toHaveAccessibleDescription(
      'At Max: Clips may differ significantly from your style input.',
    );
    slider.focus();
    await user.keyboard('{ArrowRight}');
    expect(slider).toHaveAttribute('aria-valuetext', 'High');
    expect(changes).toHaveBeenLastCalledWith('high');
    await user.keyboard('{End}{ArrowRight}');
    expect(slider).toHaveAttribute('aria-valuetext', 'Max');
    await user.keyboard('{Home}');
    expect(slider).toHaveAttribute('aria-valuetext', 'Off');
    expect(changes).toHaveBeenLastCalledWith('off');
    for (const name of ['Off', 'Normal', 'High', 'Extra', 'Max']) {
      expect(screen.getAllByText(name).length).toBeGreaterThan(0);
    }
  });
});

describe('a toggle control', () => {
  it('switches on and off with its label and Suno’s explanation, and not when read only', async () => {
    const { user, changes } = renderControl(false, ({ value, onChange }) => (
      <ToggleControl field={field('maxMode')} value={value} onChange={onChange} readOnly={false} />
    ));

    const toggle = screen.getByRole('switch', { name: 'Max Mode' });
    expect(toggle).toHaveAccessibleDescription(
      'Uses more compute to maximize consistency throughout the song. Costs 2x credits per song.',
    );
    await user.click(toggle);
    expect(toggle).toBeChecked();
    expect(changes).toHaveBeenLastCalledWith(true);
  });

  it('is disabled when read only', () => {
    renderControl(true, ({ value, onChange }) => (
      <ToggleControl field={field('personalize')} value={value} onChange={onChange} readOnly />
    ));

    const toggle = screen.getByRole('switch', { name: 'Personalize (My Taste)' });
    expect(toggle).toBeChecked();
    expect(toggle).toBeDisabled();
  });
});

describe('a choice control', () => {
  it('offers None and the field’s values, and holds null for None', async () => {
    const { user, changes } = renderControl<string | null>(null, ({ value, onChange }) => (
      <ChoiceControl
        field={field('vocalGender')}
        value={value}
        onChange={onChange}
        readOnly={false}
        noneLabel="None"
      />
    ));

    const group = screen.getByRole('radiogroup', { name: 'Vocal Gender' });
    expect(group).toHaveAccessibleDescription('Change the gender of the generated vocals');
    expect(screen.getByRole('radio', { name: 'None' })).toBeChecked();
    await user.click(screen.getByRole('radio', { name: 'Female' }));
    expect(screen.getByRole('radio', { name: 'Female' })).toBeChecked();
    expect(changes).toHaveBeenLastCalledWith('female');
    await user.click(screen.getByRole('radio', { name: 'None' }));
    expect(changes).toHaveBeenLastCalledWith(null);
  });

  it('changes nothing when read only', async () => {
    const { user, changes } = renderControl<string | null>('auto', ({ value, onChange }) => (
      <ChoiceControl field={field('durationMode')} value={value} onChange={onChange} readOnly />
    ));

    await user.click(screen.getByRole('radio', { name: 'Custom' }));
    expect(screen.getByRole('radio', { name: 'Auto' })).toBeChecked();
    expect(changes).not.toHaveBeenCalled();
  });
});

describe('a text control', () => {
  it('counts against the field’s limit and says when the text is over it', async () => {
    const { user } = renderControl('', ({ value, onChange }) => (
      <TextControl field={field('title')} value={value} onChange={onChange} readOnly={false} />
    ));

    const input = screen.getByRole('textbox', { name: 'Song Title' });
    expect(input).toHaveAccessibleDescription('0 / 100 characters');
    await user.click(input);
    await user.paste('x'.repeat(101));
    expect(input).toHaveAccessibleDescription(/101 \/ 100 characters/);
    expect(
      screen.getByText('Over the limit by 1 character. Shorten the text to save.'),
    ).toBeVisible();
  });

  it('cannot be typed in when read only', async () => {
    const { user, changes } = renderControl('Kept', ({ value, onChange }) => (
      <TextControl field={field('excludeStyles')} value={value} onChange={onChange} readOnly />
    ));

    const input = screen.getByRole('textbox', { name: 'Exclude styles' });
    await user.type(input, 'more');
    expect(input).toHaveValue('Kept');
    expect(changes).not.toHaveBeenCalled();
  });
});

describe('the model control', () => {
  it('offers the model list and Not chosen', async () => {
    const { user, changes } = renderControl<string | null>(null, ({ value, onChange }) => (
      <ModelControl
        field={field('model')}
        models={CREATE_FIELDS.models}
        value={value}
        onChange={onChange}
        readOnly={false}
      />
    ));

    const select = screen.getByRole('combobox', { name: 'Model version' });
    expect(select).toHaveValue('');
    expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual([
      'Not chosen',
      'v6',
      'v6-wild',
      'v6-mini',
    ]);
    await user.selectOptions(select, 'v6-wild');
    expect(changes).toHaveBeenLastCalledWith('v6-wild');
  });
});

describe('durations', () => {
  it('are written as minutes and seconds', () => {
    expect(formatDuration(10)).toBe('0:10');
    expect(formatDuration(125)).toBe('2:05');
    expect(formatDuration(360)).toBe('6:00');
    expect(spokenDuration(61)).toBe('1 minute 1 second');
    expect(spokenDuration(0)).toBe('0 seconds');
  });
});
