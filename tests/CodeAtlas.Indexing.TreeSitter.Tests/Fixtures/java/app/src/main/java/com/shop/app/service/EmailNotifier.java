package com.shop.app.service;

import org.springframework.web.client.RestTemplate;

import com.shop.app.model.Owner;
import com.shop.common.util.*;

public class EmailNotifier implements Notifier {
    private RestTemplate rest = new RestTemplate();

    @Override
    public void send(Owner owner) {
        String body = Strings.pad(owner.getLastName());
        rest.postForObject("/mail", body, String.class);
        Strings.join(body, 1);
    }
}