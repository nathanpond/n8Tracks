// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest';
import popupHtml from './popup.html?raw';
import { renderConnection, renderPopup } from './render.ts';

function text(id: string): string | null | undefined {
  return document.getElementById(id)?.textContent;
}

describe('renderPopup', () => {
  beforeEach(() => {
    document.documentElement.innerHTML = popupHtml;
  });

  it('shows the name, the version label, and the connection state', () => {
    renderPopup(document, { name: 'n8Tracks', version: '0.1.0' });

    expect(text('name')).toBe('n8Tracks');
    expect(text('version')).toBe('v0.1.0');
    expect(text('connection')).toBe('Not connected to n8Tracks');
  });

  it('shows the full version of a pre-release build', () => {
    renderPopup(document, {
      name: 'n8Tracks',
      version: '0.1.0',
      version_name: '0.1.0-edge.abc1234',
    });

    expect(text('version')).toBe('v0.1.0-edge.abc1234');
  });

  it('fails loudly when the page lacks an element it fills', () => {
    document.getElementById('version')?.remove();

    expect(() => {
      renderPopup(document, { name: 'n8Tracks', version: '0.1.0' });
    }).toThrow('#version');
  });

  it('shows Cannot reach n8Tracks with the address, keeping Disconnect available', () => {
    renderConnection(document, { status: 'unreachable', address: 'https://n8tracks.example.com' });

    expect(text('connection')).toBe('Cannot reach n8Tracks');
    expect(text('detail')).toContain('https://n8tracks.example.com');
    expect(document.getElementById('disconnect')?.hidden).toBe(false);
  });
});
