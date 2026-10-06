import { isRecord, useResource } from './songs';

const LANGUAGES_PATH = 'api/v1/languages';

/** A language a Song's release details can name: its code (as stored) and its English name. */
export interface Language {
  code: string;
  name: string;
}

function isLanguage(value: unknown): value is Language {
  return isRecord(value) && typeof value.code === 'string' && typeof value.name === 'string';
}

const acceptLanguages = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isLanguage)
    ? answer.items
    : undefined;

/** Every language the API accepts, by English name: the one list it also checks codes against. */
export function useLanguages() {
  return useResource(LANGUAGES_PATH, acceptLanguages);
}
