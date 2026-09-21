import { readFileSync } from "node:fs";
import { afterEach, expect, it, vi } from "vitest";

afterEach(() => vi.unstubAllGlobals());

it("opens a generated public workflow module", async () => {
    const shell = readFileSync("index.html", "utf8");
    const browserPrelude = "<script>window.matchMedia=()=>({matches:false,addEventListener(){},removeEventListener(){}})</script>";
    document.open();
    document.write(shell.replace("<head>", `<head>${browserPrelude}`));
    document.close();
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ status: "ok" }), {
        status: 200,
        headers: { "content-type": "application/json" },
    })));

    await import("../public/app.js");
    document.querySelector('[data-module="interest"]').click();

    expect(document.querySelector("#panel-interest").hidden).toBe(false);
    expect(document.querySelector("#tab-interest").getAttribute("aria-selected")).toBe("true");
});