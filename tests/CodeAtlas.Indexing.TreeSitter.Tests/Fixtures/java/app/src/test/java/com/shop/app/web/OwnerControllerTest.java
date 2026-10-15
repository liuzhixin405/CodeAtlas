package com.shop.app.web;

import static org.assertj.core.api.Assertions.assertThat;
import static com.shop.common.util.Strings.pad;

class OwnerControllerTest {
    private final OwnerController controller = new OwnerController();

    void showsOwner() {
        controller.create(null);
        assertThat(pad("x")).isEqualTo("x ");
    }
}