package com.northstar.migrated.api;

import com.northstar.migrated.domain.BankAccountRequest;
import com.northstar.migrated.repository.BankAccountRequestRepository;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

import java.util.List;

@RestController
@RequestMapping("/api/bank-account-request")
public class BankAccountRequestController {

    private final BankAccountRequestRepository repository;

    public BankAccountRequestController(BankAccountRequestRepository repository) {
        this.repository = repository;
    }

    @GetMapping
    public List<BankAccountRequest> list() {
        return repository.findAll();
    }

    @GetMapping("/{id}")
    public ResponseEntity<BankAccountRequest> get(@PathVariable Long id) {
        return repository.findById(id).map(ResponseEntity::ok).orElseGet(() -> ResponseEntity.notFound().build());
    }

    @PostMapping
    public BankAccountRequest create(@RequestBody BankAccountRequest bankAccountRequest) {
        return repository.save(bankAccountRequest);
    }
}