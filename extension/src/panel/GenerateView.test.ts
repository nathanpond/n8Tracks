// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkspaceOption } from '../adapter/workspaces.ts';
import { expectNoAxeViolations } from '../testing/a11y.ts';
import { GenerateView, optionDetail } from './GenerateView.ts';

const OPTIONS: WorkspaceOption[] = [
  { id: 'w-same', name: 'Night Drive', songCount: 0, sameName: true },
  { id: 'w-1', name: 'Studio', songCount: 3, sameName: false },
  { id: 'w-2', name: '', songCount: 1, sameName: false },
];

function view() {
  const options = {
    create: vi.fn<() => void>(),
    pick: vi.fn<(option: WorkspaceOption) => void>(),
    continueSource: vi.fn<() => void>(),
  };
  const generate = new GenerateView(document, options);
  const main = document.createElement('main');
  main.append(generate.element);
  document.body.append(main);
  const buttons = () => [...generate.element.querySelectorAll('button')];
  const button = (name: string) => buttons().find((candidate) => candidate.textContent === name);
  return { generate, options, buttons, button };
}

beforeEach(() => {
  document.title = 'Suno';
  document.documentElement.lang = 'en';
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('Generate on Suno in the panel', () => {
  it('is hidden until a request reaches the tab', () => {
    const { generate } = view();

    expect(generate.element.hidden).toBe(true);
    generate.show({ kind: 'working', step: 'Reading Suno’s workspace list' });
    expect(generate.element.hidden).toBe(false);
    expect(generate.element.querySelector('[role="status"]')?.textContent).toBe(
      'Reading Suno’s workspace list…',
    );
  });

  it('offers to create a workspace named after the Song, or to use one Suno has, same name first', async () => {
    const { generate, buttons } = view();

    generate.show({ kind: 'choose', title: 'Night Drive', reason: 'none', options: OPTIONS });

    expect(buttons().map((button) => button.textContent)).toEqual([
      'Create a workspace named “Night Drive”',
      'Use “Night Drive”',
      'Use “Studio”',
      'Use “(unnamed)”',
    ]);
    expect(
      [...generate.element.querySelectorAll('li .detail')].map((detail) => detail.textContent),
    ).toEqual([
      'Same name as the Song, no Song in it yet',
      '3 n8Tracks Songs in it',
      '1 n8Tracks Song in it',
    ]);
    expect(generate.element.querySelector('.generate-choice-reason')?.textContent).toContain(
      'has no Suno workspace yet',
    );
    expect(document.activeElement?.textContent).toBe('Create a workspace named “Night Drive”');
    await expectNoAxeViolations(document);
  });

  it('answers only the button pressed, once, and creates nothing on its own', () => {
    const { generate, options, button, buttons } = view();
    generate.show({ kind: 'choose', title: 'Night Drive', reason: 'none', options: OPTIONS });
    expect(options.create).not.toHaveBeenCalled();
    expect(options.pick).not.toHaveBeenCalled();

    button('Use “Studio”')?.click();

    expect(options.pick).toHaveBeenCalledExactlyOnceWith(OPTIONS[1]);
    expect(options.create).not.toHaveBeenCalled();
    expect(buttons().every((candidate) => candidate.disabled)).toBe(true);
  });

  it('says why when the Song’s workspace was not found, and when it stopped', async () => {
    const { generate, options, button } = view();

    generate.show({ kind: 'choose', title: 'Night Drive', reason: 'unavailable', options: [] });
    expect(generate.element.querySelector('.generate-choice-reason')?.textContent).toContain(
      'marked it Unavailable',
    );
    expect(generate.element.textContent).toContain('Suno listed no other workspace.');
    button('Create a workspace named “Night Drive”')?.click();
    expect(options.create).toHaveBeenCalledOnce();

    generate.show({ kind: 'stopped', message: 'You are not signed in to Suno.' });
    expect(generate.element.querySelector('[role="alert"]')?.textContent).toBe(
      'Generate on Suno stopped: You are not signed in to Suno.',
    );
    await expectNoAxeViolations(document);
  });

  it('asks the user to select the Song’s workspace when Suno has two of its name, offering no button (#335)', async () => {
    const { generate, buttons } = view();

    generate.show({ kind: 'select', name: 'Night Drive' });

    expect(generate.element.querySelector('[role="alert"]')?.textContent).toBe(
      'Suno has more than one workspace named “Night Drive”. Select the Song’s one in Suno’s workspace list.',
    );
    expect(generate.element.textContent).toContain('which it checks by ID');
    expect(buttons()).toEqual([]);
    await expectNoAxeViolations(document);
  });

  it('says what the user’s Create came to, or that its clips were not recorded (#149)', async () => {
    const { generate, buttons } = view();

    generate.show({
      kind: 'recorded',
      recorded: true,
      message: '2 Generations recorded on n8-1-v1.',
    });
    expect(generate.element.querySelector('[role="status"]')?.textContent).toBe(
      '2 Generations recorded on n8-1-v1.',
    );
    expect(generate.element.textContent).toContain('The extension never clicks Create.');
    expect(buttons()).toEqual([]);
    await expectNoAxeViolations(document);

    generate.show({
      kind: 'recorded',
      recorded: false,
      message: 'Not recorded; a sync will bring them in.',
    });
    expect(generate.element.querySelector('[role="alert"]')?.textContent).toBe(
      'Not recorded; a sync will bring them in.',
    );
    await expectNoAxeViolations(document);
  });

  it('asks the user to load the source by hand, and Continue asks for it to be checked again (#148)', async () => {
    const { generate, options, button } = view();

    generate.show({
      kind: 'source',
      message: 'The source on Suno’s form is not the Version’s “Night Drive”.',
    });

    expect(generate.element.querySelector('[role="alert"]')?.textContent).toBe(
      'The source on Suno’s form is not the Version’s “Night Drive”.',
    );
    expect(document.activeElement?.textContent).toBe('Continue');
    await expectNoAxeViolations(document);
    button('Continue')?.click();
    expect(options.continueSource).toHaveBeenCalledOnce();
  });

  it('describes a workspace by the Songs in it', () => {
    expect(optionDetail({ id: 'a', name: 'A', songCount: 0, sameName: false })).toBe(
      'No n8Tracks Song in it',
    );
  });
});
