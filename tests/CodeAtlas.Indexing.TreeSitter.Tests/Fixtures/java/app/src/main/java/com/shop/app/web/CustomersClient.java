package com.shop.app.web;

import java.util.List;

import org.springframework.web.reactive.function.client.WebClient;

import com.shop.app.model.Owner;

public class CustomersClient {
    private final WebClient.Builder builder;

    public CustomersClient(WebClient.Builder builder) {
        this.builder = builder;
    }

    public Object load(List<Owner> owners) {
        owners.stream().count();
        return builder.build().get().uri("/owners").retrieve();
    }

    public String name(OwnerRequest request, Owner owner) {
        owner.getAddress();
        owner.missing();
        return request.lastName();
    }
}