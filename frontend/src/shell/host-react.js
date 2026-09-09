// This shell's React, republished at a stable URL so a DEVICE's module bundle can
// reach it through the import map in index.html instead of carrying its own copy.
// Two React instances is the one thing that genuinely breaks — hooks throw
// immediately — so this file is load-bearing.
//
// ── It must be a superset of the device shell's facade ────────────────────────
// A module is built against Strux's `frontend/src/shell/host-react.js` and only
// ever imports names that one exports. This list is a verbatim copy of it, and it
// has to stay that way or better: a name Strux publishes and this file does not is
// a module that loads here and then fails on first use, in a browser, on somebody
// else's device. The two lists moving together is easier to hold than a check
// spanning two repositories, and the contract's `hostApi` integer is what covers a
// React major actually changing.
//
// ── Why every name is spelled out ─────────────────────────────────────────────
// `export * from "react"` compiles, emits, and silently produces NOTHING usable.
// React 19's package is CommonJS, so a star re-export has no statically known names
// to forward: the emitted chunk exported a handful of mangled internals and a
// module's `import { useState } from "react"` would have got undefined. It was
// caught upstream by reading the emitted chunk, and it would not have been caught
// by a build that merely succeeded.
//
// JavaScript rather than TypeScript because @types/react uses `export =`, which
// TypeScript will not combine with a re-export of this shape. There is nothing to
// type: the file declares nothing.
//
// Emitted without a content hash (see vite.config.ts) so the import map can be a
// static snippet rather than something a build plugin injects.

export {
  default,
  Children,
  Component,
  Fragment,
  Profiler,
  PureComponent,
  StrictMode,
  Suspense,
  cache,
  cloneElement,
  createContext,
  createElement,
  createRef,
  forwardRef,
  isValidElement,
  lazy,
  memo,
  startTransition,
  use,
  useActionState,
  useCallback,
  useContext,
  useDebugValue,
  useDeferredValue,
  useEffect,
  useId,
  useImperativeHandle,
  useInsertionEffect,
  useLayoutEffect,
  useMemo,
  useOptimistic,
  useReducer,
  useRef,
  useState,
  useSyncExternalStore,
  useTransition,
  version,
} from "react"
