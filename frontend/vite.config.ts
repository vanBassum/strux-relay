import path from "node:path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig } from "vite"

// Where Kestrel listens in development; see api/Properties/launchSettings.json.
const api = "http://localhost:8080"

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": path.resolve(import.meta.dirname, "./src"),
    },
  },
  build: {
    // Straight into the API project, which serves it with UseStaticFiles. Unlike
    // Strux's own frontend there is no gzip step: nothing here lands on a flash
    // partition, so compression is the server's job at serve time.
    outDir: path.resolve(import.meta.dirname, "../api/wwwroot"),
    emptyOutDir: true,
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
