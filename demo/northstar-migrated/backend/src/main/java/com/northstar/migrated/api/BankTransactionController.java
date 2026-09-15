package com.northstar.migrated.api;

import com.northstar.migrated.domain.BankTransaction;
import com.northstar.migrated.repository.BankTransactionRepository;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

import java.util.List;

@RestController
@RequestMapping("/api/bank-transaction")
public class BankTransactionController {

    private final BankTransactionRepository repository;

    public BankTransactionController(BankTransactionRepository repository) {
        this.repository = repository;
    }

    @GetMapping
    public List<BankTransaction> list() {
        return repository.findAll();
    }

    @GetMapping("/{id}")
    public ResponseEntity<BankTransaction> get(@PathVariable Long id) {
        return repository.findById(id).map(ResponseEntity::ok).orElseGet(() -> ResponseEntity.notFound().build());
    }

    @PostMapping
    public BankTransaction create(@RequestBody BankTransaction bankTransaction) {
        return repository.save(bankTransaction);
    }
}