package com.northstar.migrated.banking;

import com.northstar.migrated.banking.BankingContracts.ErrorResponse;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

/**
 * One answer for every path under /api that the workflow service does not implement.
 *
 * Without this, an unknown API path falls through to whatever else is mapped — in a Spring Boot
 * application serving a browser client that is the static resource handler, which answers with the
 * client's HTML. A caller expecting JSON then has to parse a page to discover the route is gone.
 * This returns the same JSON error shape every other failure uses, with a 404.
 *
 * Spring matches the most specific pattern first, so every real route above still wins over this one.
 */
@RestController
public class BankingApiFallbackController {

    @RequestMapping({"/api", "/api/**"})
    public ResponseEntity<ErrorResponse> unknown() {
        return ResponseEntity.status(HttpStatus.NOT_FOUND)
                .body(new ErrorResponse("No such endpoint."));
    }
}
