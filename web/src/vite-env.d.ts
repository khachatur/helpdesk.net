/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** DevExtreme licence key. Set it in web/.env.local, which is not committed. */
  readonly VITE_DEVEXTREME_LICENSE_KEY?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
