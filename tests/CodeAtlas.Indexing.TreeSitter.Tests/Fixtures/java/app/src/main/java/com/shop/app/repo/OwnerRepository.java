package com.shop.app.repo;

import java.util.List;

import org.springframework.data.jpa.repository.JpaRepository;

import com.shop.app.model.Owner;

public interface OwnerRepository extends JpaRepository<Owner, Integer> {
    List<Owner> findByLastName(String lastName);
}