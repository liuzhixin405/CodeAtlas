package com.shop.common.util;

public final class Strings {
    private Strings() {
    }

    public static String pad(String s) {
        return pad(s, 2);
    }

    public static String pad(String s, int width) {
        return s;
    }

    public static String join(String a, int b) {
        return a + b;
    }

    public static String join(String a, String b) {
        return a + b;
    }
}