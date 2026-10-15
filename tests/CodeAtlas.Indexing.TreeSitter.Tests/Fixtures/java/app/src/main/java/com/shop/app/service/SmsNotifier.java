package com.shop.app.service;

import com.shop.app.model.Owner;

public class SmsNotifier implements Notifier {
    @Override
    public void send(Owner owner) {
        owner.rename("sms");
    }
}