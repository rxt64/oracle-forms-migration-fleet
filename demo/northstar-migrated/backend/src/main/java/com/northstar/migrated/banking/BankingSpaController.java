package com.northstar.migrated.banking;

import org.springframework.stereotype.Controller;
import org.springframework.web.bind.annotation.GetMapping;

/**
 * Serves the browser client's own routes.
 *
 * The client is a single page: a bookmarked or refreshed module path has no file behind it and would
 * otherwise 404. This forwards those paths to the client shell so the page can render the module
 * itself.
 *
 * Two exclusions are deliberate and both are in the pattern rather than in a runtime check:
 *
 * <ul>
 *   <li><b>{@code /api} is never intercepted.</b> The negative lookahead keeps this mapping off the
 *       API namespace entirely, so an unknown API path reaches the JSON 404 rather than being handed
 *       a page of HTML.</li>
 *   <li><b>Files are never intercepted.</b> The pattern excludes any segment containing a dot, so
 *       {@code /app.js} and {@code /styles.css} still come from the static resource handler.</li>
 * </ul>
 */
@Controller
public class BankingSpaController {

    @GetMapping("/{route:(?!api$|healthz$)[^.]*}")
    public String clientRoute() {
        return "forward:/index.html";
    }
}
