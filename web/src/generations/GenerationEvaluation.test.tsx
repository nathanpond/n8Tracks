import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { COMMENT_MAXIMUM_LENGTH } from '../api/generations';
import { jsonResponse, renderApp } from '../test/helpers';
import { testComment, testGeneration, testVersion, versionServer } from '../test/versionServer';
import { commentLength, ratingForKey } from './evaluationRules';

const ONE = testVersion('1', { current: true, isFrozen: true });
const G1 = 'n8-7-v1-g1';

/** The Song page with Generation 1's panel open, its Version's row expanded beneath it. */
async function openPanel(path = `/songs/n8-7/generations/${G1}`) {
  const rendered = renderApp(path);
  const panel = await screen.findByRole('dialog', { name: `Generation ${G1}` });
  await waitFor(() => {
    expect(panel).toBeVisible();
  });
  return { ...rendered, panel };
}

function section() {
  return screen.getByRole('region', { name: 'Versions and Generations' });
}

function generationRow(shortcode: string): HTMLElement {
  const row = section().querySelector(`tr[data-generation="${shortcode}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`Generation ${shortcode} is not listed.`);
  }
  return row;
}

function versionRow(number: string): HTMLElement {
  const row = section().querySelector(`tr[data-version-row="${number}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`Version ${number} is not listed.`);
  }
  return row;
}

/** The radio group named "Rating of <shortcode>" inside `container`. */
function stars(container: HTMLElement, shortcode = G1) {
  return within(container).getByRole('radiogroup', { name: `Rating of ${shortcode}` });
}

function checkedStars(group: HTMLElement): string | null {
  return (
    within(group)
      .queryAllByRole('radio')
      .find((radio) => radio.getAttribute('aria-checked') === 'true')
      ?.getAttribute('aria-label') ?? null
  );
}

/** A promise and the function that settles it, to hold a fake answer back. */
function deferred<T>() {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

describe('the rating rules', () => {
  it('moves by one star on the arrow keys, jumps with Home and End, and clears below one or on Delete', () => {
    expect(ratingForKey('ArrowRight', null)).toBe(1);
    expect(ratingForKey('ArrowUp', 4)).toBe(5);
    expect(ratingForKey('ArrowRight', 5)).toBe(5);
    expect(ratingForKey('ArrowLeft', 3)).toBe(2);
    expect(ratingForKey('ArrowDown', 1)).toBeNull();
    expect(ratingForKey('Home', 4)).toBe(1);
    expect(ratingForKey('End', null)).toBe(5);
    expect(ratingForKey('Delete', 3)).toBeNull();
    expect(ratingForKey('Backspace', 3)).toBeNull();
    expect(ratingForKey('a', 3)).toBeUndefined();
  });

  it('counts a comment as it will be kept, trimmed, and says by how much one is too long', () => {
    expect(commentLength('  Good  ').counter).toBe('4 of 2,000 characters');
    expect(commentLength('x'.repeat(COMMENT_MAXIMUM_LENGTH)).error).toBeUndefined();
    expect(commentLength('x'.repeat(COMMENT_MAXIMUM_LENGTH + 1)).error).toBe(
      'This comment is 1 character too long. Shorten it to save it.',
    );
    expect(commentLength('x'.repeat(COMMENT_MAXIMUM_LENGTH + 12)).error).toContain(
      '12 characters too long',
    );
  });
});

describe('rating a Generation', () => {
  it('sets four stars in the panel, which the row and the Version’s highest rating show at once', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1), testGeneration('1', 2, { rating: 2 })];
    const { panel } = await openPanel();

    await user.click(within(stars(panel)).getByRole('radio', { name: '4 stars' }));

    expect(checkedStars(stars(panel))).toBe('4 stars');
    expect(within(panel).getByTestId('generation-panel-rating')).toHaveTextContent('4 of 5 stars');
    expect(checkedStars(stars(generationRow(G1)))).toBe('4 stars');
    expect(within(versionRow('1')).getByTestId('highest-rating')).toHaveTextContent('4 of 5 stars');
    await waitFor(() => {
      expect(server.generationWrites).toHaveLength(1);
    });
    expect(server.generationWrites[0]).toMatchObject({
      method: 'PATCH',
      ifMatch: '"1"',
      body: { rating: 4 },
    });
    expect(server.generationWrites[0]?.path).toMatch(/\/api\/v1\/generations\/[^/]+$/);
    await waitFor(() => {
      expect(server.generations[0]?.rating).toBe(4);
    });
  });

  it('clears the rating when the current star is clicked again, from the row too', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1, { rating: 3 })];
    const { panel } = await openPanel();

    await user.click(within(stars(generationRow(G1))).getByRole('radio', { name: '3 stars' }));

    expect(checkedStars(stars(panel))).toBeNull();
    expect(within(panel).getByTestId('generation-panel-rating')).toHaveTextContent('Not rated');
    expect(within(versionRow('1')).getByTestId('highest-rating')).toHaveTextContent('Not rated');
    await waitFor(() => {
      expect(server.generationWrites.map((write) => write.body)).toEqual([{ rating: null }]);
    });
  });

  it('is operated with the arrow keys, changing at once with focus on the checked star', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1)];
    const { panel } = await openPanel();
    const group = stars(panel);

    // One tab stop: the first star while there is no rating.
    const first = within(group).getByRole('radio', { name: '1 star' });
    expect(first).toHaveAttribute('tabindex', '0');
    expect(within(group).getByRole('radio', { name: '2 stars' })).toHaveAttribute('tabindex', '-1');
    first.focus();

    await user.keyboard('{ArrowRight}');
    expect(checkedStars(group)).toBe('1 star');
    await user.keyboard('{ArrowRight}{ArrowRight}');
    expect(checkedStars(group)).toBe('3 stars');
    expect(within(group).getByRole('radio', { name: '3 stars' })).toHaveFocus();
    await user.keyboard('{End}');
    expect(checkedStars(group)).toBe('5 stars');
    await user.keyboard('{ArrowLeft}');
    expect(checkedStars(group)).toBe('4 stars');
    expect(within(group).getByRole('radio', { name: '4 stars' })).toHaveFocus();

    // The last value wins, whatever was sent on the way.
    await waitFor(() => {
      expect(server.generations[0]?.rating).toBe(4);
    });
    await user.keyboard('{Delete}');
    expect(checkedStars(group)).toBeNull();
    await waitFor(() => {
      expect(server.generations[0]?.rating).toBeNull();
    });
  });

  it('sends one change at a time and the latest value wins', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1)];
    const { panel } = await openPanel();
    const held = deferred<Response>();
    server.nextGenerationWrite = async () => {
      const answer = await held.promise;
      server.generations = [testGeneration('1', 1, { rating: 2, revision: 2 })];
      return answer;
    };

    await user.click(within(stars(panel)).getByRole('radio', { name: '2 stars' }));
    await user.click(within(stars(panel)).getByRole('radio', { name: '3 stars' }));
    await user.click(within(stars(panel)).getByRole('radio', { name: '5 stars' }));

    // Only the first is out; the other two wait for it, and only the last of them is sent.
    expect(server.generationWrites).toHaveLength(1);
    expect(checkedStars(stars(panel))).toBe('5 stars');
    held.resolve(jsonResponse(200, testGeneration('1', 1, { rating: 2, revision: 2 })));

    await waitFor(() => {
      expect(server.generationWrites.map((write) => [write.ifMatch, write.body])).toEqual([
        ['"1"', { rating: 2 }],
        ['"2"', { rating: 5 }],
      ]);
    });
    await waitFor(() => {
      expect(server.generations[0]?.rating).toBe(5);
    });
    expect(checkedStars(stars(panel))).toBe('5 stars');
  });

  it('sends a rating refused as stale once more with the current revision', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1)];
    const { panel } = await openPanel();
    const id = server.generations[0]?.id ?? '';
    server.rateElsewhere(id, 2);

    await user.click(within(stars(panel)).getByRole('radio', { name: '4 stars' }));

    await waitFor(() => {
      expect(server.generations[0]?.rating).toBe(4);
    });
    expect(server.generationWrites.map((write) => write.ifMatch)).toEqual(['"1"', '"2"']);
    expect(screen.queryByTestId('rating-problem')).toBeNull();
    expect(checkedStars(stars(panel))).toBe('4 stars');
  });

  it('shows the rating n8Tracks has, and says so, when the write is stale twice', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1)];
    const { panel } = await openPanel();
    const current = testGeneration('1', 1, { rating: 1, revision: 3 });
    server.nextGenerationWrite = () => {
      server.nextGenerationWrite = () => jsonResponse(409, { code: 'revision_conflict', current });
      return jsonResponse(409, {
        code: 'revision_conflict',
        current: { ...current, revision: 2, rating: 2 },
      });
    };

    await user.click(within(stars(panel)).getByRole('radio', { name: '4 stars' }));

    expect(await within(panel).findByRole('alert')).toHaveTextContent(
      `The rating of ${G1} was changed somewhere else at the same time, so yours was not saved. It now shows 1 of 5 stars.`,
    );
    expect(checkedStars(stars(panel))).toBe('1 star');
    expect(server.generationWrites.map((write) => write.ifMatch)).toEqual(['"1"', '"2"']);
  });

  it('works on an archived Generation of an archived Version', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([
      testVersion('1', { current: true }),
      testVersion('2', { archived: true, isFrozen: true }),
    ]);
    server.generations = [testGeneration('2', 1, { state: 'archived', remoteState: 'trashed' })];
    renderApp('/songs/n8-7/generations/n8-7-v2-g1');
    const panel = await screen.findByRole('dialog', { name: 'Generation n8-7-v2-g1' });

    await user.click(within(stars(panel, 'n8-7-v2-g1')).getByRole('radio', { name: '2 stars' }));

    await waitFor(() => {
      expect(server.generations[0]?.rating).toBe(2);
    });
  });
});

describe('commenting on a Generation', () => {
  it('adds, edits, and deletes comments, each showing when it was written and whether it was edited', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1)];
    const { panel } = await openPanel();
    expect(within(panel).getByTestId('no-comments')).toHaveTextContent('No comments yet');

    const box = within(panel).getByRole('textbox', { name: 'New comment' });
    const add = within(panel).getByRole('button', { name: 'Add comment' });
    expect(add).toBeDisabled();
    await user.type(box, 'Good piano intro');
    expect(within(panel).getByText('16 of 2,000 characters')).toBeInTheDocument();
    await user.click(add);

    const list = await within(panel).findByRole('list');
    expect(within(list).getByText('Good piano intro')).toBeVisible();
    expect(within(list).getByRole('time')).toHaveAttribute('dateTime', '2026-10-02T09:00:00Z');
    expect(within(list).queryByTestId('comment-edited')).toBeNull();
    expect(box).toHaveValue('');
    expect(within(generationRow(G1)).getByTestId('generation-comment-count')).toHaveTextContent(
      '1',
    );

    // Edit it in place: the edited time shows.
    await user.click(within(list).getByRole('button', { name: 'Edit comment 1' }));
    const edit = within(list).getByRole('textbox', { name: 'Edit comment 1' });
    expect(edit).toHaveFocus();
    await user.clear(edit);
    await user.type(edit, 'Great piano intro');
    await user.click(within(list).getByRole('button', { name: 'Save' }));
    expect(await within(list).findByText('Great piano intro')).toBeVisible();
    expect(within(list).getByTestId('comment-edited')).toHaveTextContent('Edited');
    expect(within(list).getByRole('button', { name: 'Edit comment 1' })).toHaveFocus();

    // A second one, then delete the first after confirming (Keep it cancels).
    await user.type(box, 'Second note');
    await user.click(add);
    expect(await within(list).findByText('Second note')).toBeVisible();
    await user.click(within(list).getByRole('button', { name: 'Delete comment 1' }));
    expect(within(list).getByRole('button', { name: 'Keep it' })).toHaveFocus();
    await user.click(within(list).getByRole('button', { name: 'Keep it' }));
    expect(within(list).getByText('Great piano intro')).toBeVisible();
    await user.click(within(list).getByRole('button', { name: 'Delete comment 1' }));
    await user.click(within(list).getByRole('button', { name: 'Delete comment 1' }));

    await waitFor(() => {
      expect(within(panel).queryByText('Great piano intro')).toBeNull();
    });
    expect(within(panel).getByText('Second note')).toBeVisible();
    expect(server.generations[0]?.comments.map((comment) => comment.text)).toEqual(['Second note']);
    expect(server.generationWrites.map((write) => write.method)).toEqual([
      'POST',
      'PATCH',
      'POST',
      'DELETE',
    ]);
    expect(server.generationWrites[1]).toMatchObject({ ifMatch: '"1"' });
    expect(server.generationWrites[3]).toMatchObject({ ifMatch: '"2"' });
  });

  it('shows a counter and refuses a too long comment without cutting it', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1)];
    const { panel } = await openPanel();
    const box = within(panel).getByRole('textbox', { name: 'New comment' });
    const text = 'x'.repeat(COMMENT_MAXIMUM_LENGTH + 3);

    await user.click(box);
    await user.paste(text);

    expect(box).toHaveValue(text);
    expect(box).toHaveAttribute('aria-invalid', 'true');
    expect(within(panel).getByText('2,003 of 2,000 characters')).toBeInTheDocument();
    expect(
      within(panel).getByText('This comment is 3 characters too long. Shorten it to save it.'),
    ).toBeInTheDocument();
    expect(within(panel).getByRole('button', { name: 'Add comment' })).toBeDisabled();

    // Whitespace alone is no comment either.
    await user.clear(box);
    await user.type(box, '   ');
    expect(within(panel).getByRole('button', { name: 'Add comment' })).toBeDisabled();
    expect(server.generationWrites).toEqual([]);
  });

  it('drops a comment deleted elsewhere from the list without an error', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1, { comments: [testComment(1), testComment(2)] })];
    const { panel } = await openPanel();
    const list = within(panel).getByRole('list');
    server.generations = [testGeneration('1', 1, { comments: [testComment(2)] })];

    await user.click(within(list).getByRole('button', { name: 'Delete comment 1' }));
    await user.click(within(list).getByRole('button', { name: 'Delete comment 1' }));

    await waitFor(() => {
      expect(within(panel).queryByText('Comment 1')).toBeNull();
    });
    expect(within(panel).getByText('Comment 2')).toBeVisible();
    expect(within(panel).queryByRole('alert')).toBeNull();
  });

  it('keeps an edit of a comment changed elsewhere from being saved, and shows the saved text', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1, { comments: [testComment(1)] })];
    const { panel } = await openPanel();
    server.generations = [
      testGeneration('1', 1, {
        comments: [
          testComment(1, {
            text: 'Changed elsewhere',
            editedAt: '2026-10-02T08:00:00Z',
            revision: 2,
          }),
        ],
      }),
    ];

    await user.click(within(panel).getByRole('button', { name: 'Edit comment 1' }));
    await user.type(within(panel).getByRole('textbox', { name: 'Edit comment 1' }), ' and more');
    await user.click(within(panel).getByRole('button', { name: 'Save' }));

    expect(await within(panel).findByRole('alert')).toHaveTextContent(
      'This comment was changed somewhere else since you opened it, so your edit was not saved.',
    );
    expect(within(panel).getByText('Changed elsewhere')).toBeVisible();
    expect(within(panel).getByTestId('comment-edited')).toBeInTheDocument();
  });
});
