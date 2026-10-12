import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The relay binary serves the built bundle from `web/dist` and the API from the same origin,
// so there is no base-path or proxy to configure for production. In `vite dev` we proxy the
// API to a locally-running relay for a fast UI loop.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
  },
  server: {
    proxy: {
      "/api": "http://127.0.0.1:8080",
    },
  },
});
