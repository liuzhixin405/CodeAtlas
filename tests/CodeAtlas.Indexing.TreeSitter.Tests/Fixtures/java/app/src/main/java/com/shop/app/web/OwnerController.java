package com.shop.app.web;

import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

import com.shop.app.model.Owner;
import com.shop.app.service.OwnerService;

@RestController
public class OwnerController {
    @Autowired
    private OwnerService service;

    @GetMapping(
        value = "/owners/{name}",
        produces = "application/json")
    public Owner show(String name) {
        Owner owner = service.find(name);
        var address = new Owner.Address();
        address.format();
        service.find(name).getLastName();
        return owner;
    }

    public void create(Owner owner) {
        if (owner.isNew()) {
            service.save(owner);
        }
        this.audit(owner);
    }

    private void audit(Owner owner) {
        Owner.Address a = null;
        a.format();
    }
}