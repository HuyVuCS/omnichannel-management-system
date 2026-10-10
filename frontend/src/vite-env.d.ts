/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Origin of the API when the frontend is served from another origin; not set = the origin of the page. */
  readonly VITE_API_BASE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
