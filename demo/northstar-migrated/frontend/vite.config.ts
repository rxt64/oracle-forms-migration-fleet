import { defineConfig } from "vite";

// The client is plain HTML, CSS and JavaScript. Vite emits the shell and copies public/ verbatim, so
// the built output keeps the file names the shell asks for.
export default defineConfig({ build: { outDir: "dist", emptyOutDir: true } });
