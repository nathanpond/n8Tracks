/**
 * The panel's stylesheet, inside its shadow root so Suno's styles and the panel's never meet.
 * Colours follow the system colour scheme; each text pair is at least 4.5:1 (the popup's pairs).
 */
export const PANEL_STYLES = `
:host {
  all: initial;
  position: fixed;
  top: 0;
  right: 0;
  z-index: 2147483647;
  display: block;
  width: 360px;
  max-width: 100vw;
  height: 100vh;
  --background: #ffffff;
  --text: #1a1b1e;
  --secondary-text: #495057;
  --border: #ced4da;
  --warning-background: #fff3bf;
  --warning-text: #5c3c00;
  --problem-text: #a51111;
  --button-background: #f1f3f5;
}

:host([hidden]) {
  display: none !important;
}

@media (prefers-color-scheme: dark) {
  :host {
    --background: #1a1b1e;
    --text: #f1f3f5;
    --secondary-text: #c1c2c5;
    --border: #495057;
    --warning-background: #3d3000;
    --warning-text: #ffe066;
    --problem-text: #ffa8a8;
    --button-background: #2c2e33;
  }
}

.panel {
  box-sizing: border-box;
  height: 100%;
  overflow-y: auto;
  padding: 12px 16px;
  border-left: 1px solid var(--border);
  background: var(--background);
  color: var(--text);
  font:
    14px/1.4 system-ui,
    sans-serif;
  color-scheme: light dark;
}

header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

h2 {
  margin: 0;
  font-size: 16px;
}

h2:focus-visible,
button:focus-visible,
.download:focus-visible {
  outline: 2px solid var(--text);
  outline-offset: 2px;
}

h3 {
  margin: 16px 0 4px;
  font-size: 14px;
}

h4 {
  margin: 8px 0 2px;
  font-size: 13px;
  color: var(--secondary-text);
}

p {
  margin: 4px 0 0;
}

.versions,
.detail {
  color: var(--secondary-text);
}

.connection {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

.warning {
  margin-top: 12px;
  padding: 8px;
  border-radius: 4px;
  background: var(--warning-background);
  color: var(--warning-text);
}

ul {
  margin: 4px 0 0;
  padding-left: 18px;
}

[data-state='not-working'] .workflow-state {
  color: var(--problem-text);
}

[data-state='not-checked'] .workflow-state,
[data-state='waiting'] .workflow-state,
.features [aria-disabled='true'] .feature-status {
  color: var(--secondary-text);
}

button {
  font: inherit;
  padding: 4px 10px;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: var(--button-background);
  color: var(--text);
  cursor: pointer;
}

.check {
  margin-top: 12px;
}

.statement,
.preparing {
  color: var(--secondary-text);
}

.download {
  display: inline-block;
  margin-top: 8px;
  color: var(--text);
  text-decoration: underline;
}

.download[hidden],
.preparing[hidden] {
  display: none;
}
`;
