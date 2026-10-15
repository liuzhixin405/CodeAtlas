package com.shop.app.service;

import com.shop.app.model.Owner;

public interface OwnerService {
    Owner find(String lastName);

    void save(Owner owner);
}