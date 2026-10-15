package com.shop.app.model;

import com.shop.common.BaseEntity;

public class Owner extends BaseEntity {
    private String lastName;
    private Address address;

    public String getLastName() {
        return lastName;
    }

    public void rename(String lastName) {
        this.lastName = lastName;
        touch();
        audit.record("rename");
    }

    @Override
    public boolean isNew() {
        return super.isNew();
    }

    public static class Address {
        private String city;

        public String format() {
            return city;
        }
    }
}