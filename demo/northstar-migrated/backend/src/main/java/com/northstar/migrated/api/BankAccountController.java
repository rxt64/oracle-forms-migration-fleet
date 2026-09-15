package com.northstar.migrated.api;

import com.northstar.migrated.domain.BankAccount;
import com.northstar.migrated.repository.BankAccountRepository;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

import java.util.List;

@RestController
@RequestMapping("/api/bank-account")
public class BankAccountController {

    private final BankAccountRepository repository;

    public BankAccountController(BankAccountRepository repository) {
        this.repository = repository;
    }

    @GetMapping
    public List<BankAccount> list() {
        return repository.findAll();
    }

    @GetMapping("/{id}")
    public ResponseEntity<BankAccount> get(@PathVariable Long id) {
        return repository.findById(id).map(ResponseEntity::ok).orElseGet(() -> ResponseEntity.notFound().build());
    }

    @PostMapping
    public BankAccount create(@RequestBody BankAccount bankAccount) {
        return repository.save(bankAccount);
    }
}