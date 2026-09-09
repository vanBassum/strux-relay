import path from "node:path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig, type Plugin } from "vite"

// ── The import map's targets do not exist in dev ──────────────────────────────
// `assets/host-react.js` is emitted by the BUILD, from rollupOptions.input below.
// `pnpm dev` serves source out of /src and emits no assets at all, so a firmware
// module's bare `react` resolved through the import map to a 404 and the module failed
// to load — reported by the browser as the dynamic import itself failing, which points
// at the bundle rather than at its dependency. Production was fine the whole time: the
// seam was only ever exercised by a build.
//
// A REDIRECT, not a handler that serves the bytes. Sending the browser to the source
// file puts it through Vite's normal transform, so the facade's own `react` import is
// rewritten to the same pre-bundled dependency the app imports — which is the point of
// the facade. Serving these bytes here would leave that import bare and unresolvable.
function hostFacadesInDev(): Plugin {
  return {
    name: "strux:host-facades-in-dev",
    apply: "serve",
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        // The query matters: Vite appends ?import to a dynamically imported URL.
        const match = /^\/assets\/(host-react|host-jsx-runtime)\.js(\?.*)?$/.exec(
          req.url ?? "",
        )
        if (!match) return next()
        res.statusCode = 302
        res.setHeader("Location", `/src/shell/${match[1]}.js`)
        res.end()
      })
    },
  }
}

// Where Kestrel listens in development; see api/Properties/launchSettings.json.
const api = "http://localhost:8080"

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss(), hostFacadesInDev()],
  resolve: {
    alias: {
      "@": path.resolve(import.meta.dirname, "./src"),
      // Same specifier as in Strux, on purpose: a UI module's `@shell/contract`
      // import has to mean the same file in both repos, and the copy here is
      // byte-identical (see shell-contract/README.md).
      "@shell": path.resolve(import.meta.dirname, "./shell-contract"),
    },
  },
  build: {
    // Straight into the API project, which serves it with UseStaticFiles. Unlike
    // Strux's own frontend there is no gzip step: nothing here lands on a flash
    // partition, so compression is the server's job at serve time.
    outDir: path.resolve(import.meta.dirname, "../api/wwwroot"),
    emptyOutDir: true,
    rollupOptions: {
      // Without this, Rollup is free to DROP an entry's declared exports when it
      // can serve the same code as a plain shared chunk — and upstream it did:
      // host-react.js came out exporting mangled internals instead of React's
      // named bindings, so a module's `import { useState } from "react"` would
      // have resolved to undefined. "strict" makes Rollup emit a facade that
      // re-exports the real names while the implementation stays in one shared
      // chunk, which is what keeps a single React instance.
      preserveEntrySignatures: "strict",

      // Three entries, not one. `index.html` is the dashboard; the other two exist
      // so the import map in index.html can point a DEVICE's module bundle at
      // this build's React. See src/shell/host-react.js.
      input: {
        index: path.resolve(import.meta.dirname, "index.html"),
        "host-react": path.resolve(import.meta.dirname, "src/shell/host-react.js"),
        "host-jsx-runtime": path.resolve(
          import.meta.dirname,
          "src/shell/host-jsx-runtime.js",
        ),
      },
      output: {
        // The host entries carry no content hash, so the import map can be a
        // static snippet in index.html instead of something a build plugin
        // injects. They are served by Kestrel from wwwroot, where cache headers
        // are the server's business rather than the filename's.
        entryFileNames: (chunk) =>
          chunk.name.startsWith("host-")
            ? "assets/[name].js"
            : "assets/[name]-[hash].js",
      },
    },
  },
  server: {
    proxy: {
      // The dashboard's hub. ws for SignalR's WebSocket transport — without it the
      // client silently falls back to long polling, which hides transport problems
      // until production.
      "/hub": { target: api, ws: true },
      // Prefix, so this covers both the device's own pipe at /device and everything
      // under /devices/<id>/ — the browser pipe and the device's proxied assets.
      "/device": { target: api, ws: true },
      "/healthz": { target: api },
    },
  },
})
