package com.northstar.migrated.repository;

import com.northstar.migrated.domain.BankAccountRequest;
import org.springframework.data.jpa.repository.JpaRepository;

public interface BankAccountRequestRepository extends JpaRepository<BankAccountRequest, Long> {
}