package com.northstar.migrated.repository;

import com.northstar.migrated.domain.BankStaffUser;
import org.springframework.data.jpa.repository.JpaRepository;

public interface BankStaffUserRepository extends JpaRepository<BankStaffUser, Long> {
}