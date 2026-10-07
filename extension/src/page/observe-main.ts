import { installObserver } from './observe.ts';

// Registered by the service worker for https://suno.com/* in the page's own world, at
// document_start, so it wraps fetch before Suno's code first calls it.
installObserver(window);
