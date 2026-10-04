import '@mantine/core/styles.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import { App } from './App';
import { appBasePath } from './api/baseUrl';

const root = document.getElementById('root');
if (!root) {
  throw new Error('The page has no #root element.');
}

createRoot(root).render(
  <StrictMode>
    <BrowserRouter basename={appBasePath()}>
      <App />
    </BrowserRouter>
  </StrictMode>,
);
