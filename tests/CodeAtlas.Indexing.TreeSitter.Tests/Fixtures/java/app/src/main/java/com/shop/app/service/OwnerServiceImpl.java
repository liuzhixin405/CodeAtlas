package com.shop.app.service;

import java.util.List;

import lombok.RequiredArgsConstructor;
import org.springframework.stereotype.Service;

import com.shop.app.model.Owner;
import com.shop.app.repo.OwnerRepository;

@Service
@RequiredArgsConstructor
public class OwnerServiceImpl implements OwnerService {
    private final OwnerRepository owners;
    private final List<Notifier> notifiers;

    @Override
    public Owner find(String lastName) {
        List<Owner> found = owners.findByLastName(lastName);
        return found.isEmpty() ? null : found.get(0);
    }

    @Override
    public void save(Owner owner) {
        owners.save(owner);
        Notifier notifier = pick();
        notifier.send(owner);
    }

    private Notifier pick() {
        return new EmailNotifier();
    }
}