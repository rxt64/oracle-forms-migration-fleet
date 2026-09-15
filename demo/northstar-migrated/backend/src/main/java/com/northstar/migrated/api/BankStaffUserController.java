package com.northstar.migrated.api;

import com.northstar.migrated.domain.BankStaffUser;
import com.northstar.migrated.repository.BankStaffUserRepository;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

import java.util.List;

@RestController
@RequestMapping("/api/bank-staff-user")
public class BankStaffUserController {

    private final BankStaffUserRepository repository;

    public BankStaffUserController(BankStaffUserRepository repository) {
        this.repository = repository;
    }

    @GetMapping
    public List<BankStaffUser> list() {
        return repository.findAll();
    }

    @GetMapping("/{id}")
    public ResponseEntity<BankStaffUser> get(@PathVariable Long id) {
        return repository.findById(id).map(ResponseEntity::ok).orElseGet(() -> ResponseEntity.notFound().build());
    }

    @PostMapping
    public BankStaffUser create(@RequestBody BankStaffUser bankStaffUser) {
        return repository.save(bankStaffUser);
    }
}