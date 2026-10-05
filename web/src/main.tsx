import '@mantine/core/styles.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { App } from './App';
import { appBasePath } from './api/baseUrl';

const root = document.getElementById('root');
if (!root) {
  throw new Error('The page has no #root element.');
}

// A data router, so a page can hold a navigation back while it has unsaved changes (`useBlocker`).
// The routes themselves stay in App, under this one catch-all route.
const router = createBrowserRouter([{ path: '*', element: <App /> }], {
  basename: appBasePath(),
});

createRoot(root).render(
  <StrictMode>
    <RouterProvider router={router} />
  </StrictMode>,
);
