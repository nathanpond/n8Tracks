import { MantineProvider } from '@mantine/core';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { contrastRatio, palette, stateColours, type StateColourName } from '../theme/palette';
import { theme } from '../theme/theme';
import { TagLabel } from './SongParts';

// WCAG 2.1 AA: 4.5:1 for the name (1.4.3), 3:1 for the swatch and outline (1.4.11).
const TEXT = 4.5;
const NON_TEXT = 3;

const colours = Object.keys(stateColours) as StateColourName[];

describe('a Tag label', () => {
  it.each(colours)('names a %s Tag in its own colour, with a swatch and outline', (colour) => {
    render(
      <MantineProvider theme={theme}>
        <TagLabel name={`mood ${colour}`} colour={colour} />
      </MantineProvider>,
    );

    const label = screen.getByText(`mood ${colour}`).closest('[data-tag-colour]');
    expect(label).toHaveAttribute('data-tag-colour', colour);
    expect(label?.getAttribute('style')).toContain(`var(--n8-state-${colour})`);
  });

  describe.each(['light', 'dark'] as const)('in the %s scheme', (scheme) => {
    it.each(colours)('is readable as a %s label on the page', (colour) => {
      const value = stateColours[colour][scheme];
      const page = palette[scheme].body.background;

      // The name is text in the colour; the swatch and outline are graphics in it.
      expect(contrastRatio(value, page)).toBeGreaterThanOrEqual(TEXT);
      expect(contrastRatio(value, page)).toBeGreaterThanOrEqual(NON_TEXT);
    });
  });

  it('draws a colour this build does not know in the body text colour, still named', () => {
    render(
      <MantineProvider theme={theme}>
        <TagLabel name="mystery" colour="ultraviolet" />
      </MantineProvider>,
    );

    const label = screen.getByText('mystery').closest('[data-tag-colour]');
    expect(label?.getAttribute('style')).toContain('var(--mantine-color-text)');
  });
});
