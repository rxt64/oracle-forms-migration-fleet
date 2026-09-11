import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { resolve } from "node:path";
export default defineConfig({
    plugins: [react()],
    build: {
        outDir: resolve(__dirname, "../wwwroot"),
        emptyOutDir: true,
        cssCodeSplit: false,
        rollupOptions: {
            output: {
                entryFileNames: "app.js",
                chunkFileNames: "[name].js",
                assetFileNames: function (assetInfo) {
                    return assetInfo.names.some(function (name) { return name.endsWith(".css"); }) ? "styles.css" : "[name][extname]";
                },
                inlineDynamicImports: true
            }
        }
    }
});
