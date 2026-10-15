package com.shop.common;

public abstract class BaseEntity {
    protected Integer id;
    protected AuditLog audit;

    public boolean isNew() {
        return this.id == null;
    }

    public void touch() {
        audit.record("touch");
    }
}