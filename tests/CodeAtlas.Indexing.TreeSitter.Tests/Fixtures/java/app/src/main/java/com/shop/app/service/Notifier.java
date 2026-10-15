package com.shop.app.service;

import com.shop.app.model.Owner;

public interface Notifier {
    void send(Owner owner);
}