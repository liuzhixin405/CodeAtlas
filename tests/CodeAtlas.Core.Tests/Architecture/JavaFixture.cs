namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>
/// 临时目录里的 Java 小仓库：
/// shop/ —— Maven 多模块（父 pom 是 packaging pom）：common（库，带个演示 main，被 orders 依赖）、orders（Spring Boot，Feign / RestTemplate / gRPC 桩）、
///          gateway（Spring Cloud Gateway 路由 + WebClient + DiscoveryClient）、registry（Eureka 服务端）、config（配置中心）、orders-it（只有测试的模块）；
/// inventory/ —— Gradle 多项目（settings.gradle include app、lib；app 用 build.gradle.kts，project(":lib")）；
/// payments/ —— Gradle 单项目（只有 JPA 没有驱动，bootstrap.yml 里的 spring.application.name）；
/// ads/ —— Gradle 的 gRPC 服务（像 microservices-demo 的 adservice：application 插件、Dockerfile、getOrDefault("PORT", "9555")）；
/// clinic/ —— 同目录 pom.xml 与 build.gradle（spring-petclinic 的写法，Maven 优先），没写端口（Spring Boot 缺省 8080）。
/// </summary>
public sealed class JavaFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-java-" + Guid.NewGuid().ToString("N"));

    public JavaFixture()
    {
        // ── Maven 多模块 ──
        Write("shop/pom.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <parent><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-parent</artifactId><version>3.3.0</version></parent>
              <groupId>com.acme.shop</groupId>
              <artifactId>shop-parent</artifactId>
              <version>1.0.0</version>
              <packaging>pom</packaging>
              <modules>
                <module>common</module>
                <module>orders</module>
                <module>gateway</module>
                <module>registry</module>
                <module>config</module>
                <module>orders-it</module>
              </modules>
              <dependencyManagement>
                <dependencies>
                  <dependency><groupId>org.postgresql</groupId><artifactId>postgresql</artifactId><version>42.7.3</version></dependency>
                </dependencies>
              </dependencyManagement>
            </project>
            """);
        Write("shop/common/pom.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <parent><groupId>com.acme.shop</groupId><artifactId>shop-parent</artifactId><version>1.0.0</version></parent>
              <artifactId>shop-common</artifactId>
              <dependencies>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-data-redis</artifactId></dependency>
              </dependencies>
            </project>
            """);
        Write("shop/common/src/main/java/com/acme/common/Money.java", """
            package com.acme.common;

            public class Money {
                public static void main(String[] args) {
                    System.out.println("demo");
                }
            }
            """);

        Write("shop/orders/pom.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <parent><groupId>com.acme.shop</groupId><artifactId>shop-parent</artifactId><version>1.0.0</version></parent>
              <artifactId>shop-orders</artifactId>
              <dependencies>
                <dependency><groupId>${project.groupId}</groupId><artifactId>shop-common</artifactId><version>${project.version}</version></dependency>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-web</artifactId></dependency>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-data-jpa</artifactId></dependency>
                <dependency><groupId>org.postgresql</groupId><artifactId>postgresql</artifactId><scope>runtime</scope></dependency>
                <dependency><groupId>com.h2database</groupId><artifactId>h2</artifactId><scope>test</scope></dependency>
                <dependency><groupId>org.springframework.kafka</groupId><artifactId>spring-kafka</artifactId></dependency>
                <dependency><groupId>org.springframework.cloud</groupId><artifactId>spring-cloud-starter-netflix-eureka-client</artifactId></dependency>
                <dependency><groupId>org.springframework.cloud</groupId><artifactId>spring-cloud-starter-config</artifactId></dependency>
                <dependency><groupId>io.micrometer</groupId><artifactId>micrometer-registry-prometheus</artifactId></dependency>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-mail</artifactId></dependency>
                <dependency><groupId>io.grpc</groupId><artifactId>grpc-stub</artifactId></dependency>
                <dependency><groupId>org.springframework.cloud</groupId><artifactId>spring-cloud-starter-openfeign</artifactId></dependency>
              </dependencies>
            </project>
            """);
        Write("shop/orders/src/main/resources/application.yml", """
            server:
              port: ${ORDERS_PORT:8081}
            spring:
              application:
                name: orders-service
              config:
                import: optional:configserver:http://config:8888
              datasource:
                url: jdbc:postgresql://db:5432/orders
            inventory:
              url: http://inventory-service
            ---
            spring:
              config:
                activate:
                  on-profile: docker
              datasource:
                url: jdbc:mariadb://mariadb:3306/orders
            """);
        Write("shop/orders/src/main/resources/application-mysql.properties", "spring.datasource.url=${MYSQL_URL:jdbc:mysql://localhost/orders}\n# comment\n");
        Write("shop/orders/src/main/java/com/acme/orders/OrdersApplication.java", """
            package com.acme.orders;

            import org.springframework.boot.SpringApplication;
            import org.springframework.boot.autoconfigure.SpringBootApplication;

            @SpringBootApplication
            public class OrdersApplication {
                public static void main(String[] args) {
                    SpringApplication.run(OrdersApplication.class, args);
                }
            }
            """);
        Write("shop/orders/src/main/java/com/acme/orders/InventoryClient.java", """
            package com.acme.orders;

            import org.springframework.beans.factory.annotation.Value;
            import org.springframework.web.client.RestTemplate;

            public class InventoryClient {
                private final RestTemplate restTemplate = new RestTemplate();
                @Value("${inventory.url}")
                private String inventoryUrl;

                public Item get(String id) {
                    // restTemplate.getForObject("http://commented.example/x", Item.class);
                    return restTemplate.getForObject(inventoryUrl + "/items/{id}", Item.class, id);
                }

                public void charge() {
                    restTemplate.exchange("https://api.stripe.com/v1/charges", HttpMethod.POST, null, String.class);
                }

                public void reserve(String path) {
                    restTemplate.postForEntity(path, null, String.class);
                }
            }
            """);
        Write("shop/orders/src/main/java/com/acme/orders/PaymentsClient.java", """
            package com.acme.orders;

            import org.springframework.cloud.openfeign.FeignClient;
            import org.springframework.web.bind.annotation.GetMapping;
            import org.springframework.web.bind.annotation.PostMapping;

            @FeignClient(name = "payments-service")
            public interface PaymentsClient {
                @GetMapping("/payments/{id}")
                Payment get(@PathVariable String id);

                @PostMapping(value = "/payments")
                Payment create(Payment p);
            }
            """);
        Write("shop/orders/src/main/java/com/acme/orders/PricingClient.java", """
            package com.acme.orders;

            @FeignClient(value = "pricing", url = "${pricing.base-url:https://pricing.example.com}")
            interface PricingClient {
                @RequestMapping(method = RequestMethod.GET, value = "/quote")
                Quote quote();
            }
            """);
        Write("shop/orders/src/main/java/com/acme/orders/AdsClient.java", """
            package com.acme.orders;

            import io.grpc.ManagedChannel;
            import io.grpc.ManagedChannelBuilder;

            public class AdsClient {
                private final ManagedChannel channel = ManagedChannelBuilder.forTarget("ads:9555").usePlaintext().build();
                private final ads.AdServiceGrpc.AdServiceBlockingStub stub = ads.AdServiceGrpc.newBlockingStub(channel);
            }
            """);
        Write("shop/orders/src/test/java/com/acme/orders/OrdersTests.java", """
            package com.acme.orders;

            class OrdersTests {
                void x() {
                    new RestTemplate().getForObject("http://test.example", String.class);
                    CartServiceGrpc.newStub(channel);
                }
            }
            """);
        // Maven 的输出目录 target/：里面生成的源码不算
        Write("shop/orders/target/generated-sources/annotations/Gen.java", "class Gen { void x() { restTemplate.getForObject(\"http://ignored.example/x\", String.class); } }\n");

        Write("shop/gateway/pom.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <parent><groupId>com.acme.shop</groupId><artifactId>shop-parent</artifactId><version>1.0.0</version></parent>
              <artifactId>shop-gateway</artifactId>
              <dependencies>
                <dependency><groupId>org.springframework.cloud</groupId><artifactId>spring-cloud-starter-gateway</artifactId></dependency>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-webflux</artifactId></dependency>
                <dependency><groupId>org.springframework.cloud</groupId><artifactId>spring-cloud-starter-netflix-eureka-client</artifactId></dependency>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-zipkin</artifactId></dependency>
              </dependencies>
            </project>
            """);
        Write("shop/gateway/src/main/resources/application.yml", """
            spring:
              application:
                name: gateway
              cloud:
                gateway:
                  routes:
                    - id: orders
                      uri: lb://orders-service
                      predicates:
                        - Path=/api/orders/**
                      filters:
                        - StripPrefix=2
                    - id: legacy
                      uri: https://legacy.example.com
                      predicates:
                      - Path=/legacy/**
            """);
        Write("shop/gateway/src/main/java/com/acme/gateway/GatewayApplication.java", """
            package com.acme.gateway;

            @SpringBootApplication
            public class GatewayApplication {
                public static void main(String[] args) {
                    SpringApplication.run(GatewayApplication.class, args);
                }
            }
            """);
        Write("shop/gateway/src/main/java/com/acme/gateway/OrdersProxy.java", """
            package com.acme.gateway;

            import org.springframework.cloud.client.discovery.DiscoveryClient;
            import org.springframework.web.reactive.function.client.WebClient;

            public class OrdersProxy {
                private String ordersHost = "http://orders-service/";
                private final WebClient.Builder builder;
                private final DiscoveryClient discoveryClient;

                public Mono<String> get(int id) {
                    return builder.build().get()
                        .uri(ordersHost + "orders/{id}", id)
                        .retrieve().bodyToMono(String.class);
                }

                public Mono<String> payments() {
                    return WebClient.create("lb://payments-service").get().uri("/payments").retrieve().bodyToMono(String.class);
                }

                public URI inventory() {
                    return discoveryClient.getInstances("inventory-service").get(0).getUri();
                }
            }
            """);

        Write("shop/registry/pom.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <parent><groupId>com.acme.shop</groupId><artifactId>shop-parent</artifactId><version>1.0.0</version></parent>
              <artifactId>shop-registry</artifactId>
              <dependencies>
                <dependency><groupId>org.springframework.cloud</groupId><artifactId>spring-cloud-starter-netflix-eureka-server</artifactId></dependency>
              </dependencies>
            </project>
            """);
        Write("shop/registry/src/main/resources/application.properties", "spring.application.name=registry\nserver.port=8761\n");
        Write("shop/registry/src/main/java/com/acme/registry/RegistryApplication.java", """
            package com.acme.registry;

            @SpringBootApplication
            @EnableEurekaServer
            public class RegistryApplication {
                public static void main(String[] args) {
                    SpringApplication.run(RegistryApplication.class, args);
                }
            }
            """);

        Write("shop/config/pom.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <parent><groupId>com.acme.shop</groupId><artifactId>shop-parent</artifactId><version>1.0.0</version></parent>
              <artifactId>shop-config</artifactId>
              <dependencies>
                <dependency><groupId>org.springframework.cloud</groupId><artifactId>spring-cloud-config-server</artifactId></dependency>
              </dependencies>
            </project>
            """);
        Write("shop/config/src/main/resources/application.yml", "server.port: 8888\nspring:\n  cloud:\n    config:\n      server:\n        git:\n          uri: https://github.com/acme/config\n");
        Write("shop/config/src/main/java/com/acme/config/ConfigApplication.java", """
            package com.acme.config;

            @SpringBootApplication
            @EnableConfigServer
            public class ConfigApplication {
                public static void main(String[] args) {
                    SpringApplication.run(ConfigApplication.class, args);
                }
            }
            """);

        Write("shop/orders-it/pom.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <parent><groupId>com.acme.shop</groupId><artifactId>shop-parent</artifactId><version>1.0.0</version></parent>
              <artifactId>shop-orders-it</artifactId>
              <dependencies>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-test</artifactId><scope>test</scope></dependency>
              </dependencies>
            </project>
            """);
        Write("shop/orders-it/src/test/java/com/acme/it/OrdersIT.java", "package com.acme.it;\n\n@SpringBootApplication\nclass OrdersIT {\n    public static void main(String[] args) { }\n}\n");

        // ── Gradle 多项目 ──
        Write("inventory/settings.gradle", "rootProject.name = 'inventory'\ninclude 'app', 'lib'\n");
        Write("inventory/build.gradle", "plugins {\n    id 'java'\n}\n");
        Write("inventory/app/build.gradle.kts", """
            plugins {
                id("org.springframework.boot") version "3.3.0"
                java
            }

            dependencies {
                implementation(project(":lib"))
                implementation("org.springframework.boot:spring-boot-starter-web")
                implementation("org.springframework.boot:spring-boot-starter-data-jpa")
                runtimeOnly("org.hsqldb:hsqldb")
                testImplementation("org.springframework.boot:spring-boot-starter-test")
            }
            """);
        Write("inventory/app/src/main/resources/application.properties", "spring.application.name=inventory-service\nserver.port=${PORT:8082}\n");
        Write("inventory/app/src/main/java/com/acme/inventory/InventoryApp.java", """
            package com.acme.inventory;

            import java.net.URI;
            import java.net.http.HttpClient;
            import java.net.http.HttpRequest;
            import org.springframework.boot.autoconfigure.SpringBootApplication;

            @SpringBootApplication
            public class InventoryApp {
                public static void main(String[] args) throws Exception {
                    HttpClient client = HttpClient.newHttpClient();
                    HttpRequest req = HttpRequest.newBuilder(URI.create(System.getenv("PRICING_URL"))).build();
                    client.send(req, null);
                }
            }
            """);
        Write("inventory/lib/build.gradle", "dependencies {\n    implementation 'org.apache.kafka:kafka-clients:3.7.0'\n}\n");
        Write("inventory/lib/src/main/java/com/acme/lib/Util.java", "package com.acme.lib;\n\npublic class Util { }\n");

        // ── Gradle 单项目：只有 JPA，h2 只在测试里 ──
        Write("payments/build.gradle", """
            plugins {
                id 'org.springframework.boot' version '3.3.0'
                id 'java'
            }

            dependencies {
                implementation 'org.springframework.boot:spring-boot-starter-webflux'
                implementation 'org.springframework.boot:spring-boot-starter-data-jpa'
                implementation 'org.springframework.cloud:spring-cloud-starter-netflix-eureka-client'
                testImplementation 'com.h2database:h2'
            }
            """);
        Write("payments/src/main/resources/bootstrap.yml", "spring:\n  application:\n    name: payments-service\n");
        Write("payments/src/main/resources/application.yml", "server:\n  port: 8083\n");
        Write("payments/src/main/java/com/acme/payments/PaymentsApp.java", "package com.acme.payments;\n\n@SpringBootApplication\npublic class PaymentsApp {\n    public static void main(String[] args) { }\n}\n");

        // ── Gradle 的 gRPC 服务 ──
        Write("ads/settings.gradle", "rootProject.name = 'hipstershop'\n");
        Write("ads/build.gradle", """
            plugins {
                id 'application'
            }
            def grpcVersion = "1.84.0"
            dependencies {
                implementation "io.grpc:grpc-stub:${grpcVersion}",
                        "io.grpc:grpc-netty:${grpcVersion}",
                        "com.google.cloud:google-cloud-pubsub:1.120.0"
                runtimeOnly "io.netty:netty-tcnative-boringssl-static:2.0.84.Final"
            }
            """);
        Write("ads/Dockerfile", "FROM eclipse-temurin:21\nEXPOSE 9555\nENTRYPOINT [\"/app/bin/AdService\"]\n");
        Write("ads/src/main/java/ads/AdService.java", """
            package ads;

            import io.grpc.Server;
            import io.grpc.ServerBuilder;

            public final class AdService {
              private Server server;

              private void start() throws java.io.IOException {
                int port = Integer.parseInt(System.getenv().getOrDefault("PORT", "9555"));
                server = ServerBuilder.forPort(port).addService(new AdServiceImpl()).build().start();
              }

              private static class AdServiceImpl extends ads.AdServiceGrpc.AdServiceImplBase {
              }

              private static class HealthImpl extends io.grpc.health.v1.HealthGrpc.HealthImplBase {
              }

              public static void main(String[] args) throws Exception {
                new AdService().start();
              }
            }
            """);
        Write("ads/src/main/java/ads/AdServiceClient.java", """
            package ads;

            public class AdServiceClient {
              public static void main(String[] args) {
                ManagedChannel channel = ManagedChannelBuilder.forAddress("localhost", 9555).usePlaintext().build();
                AdServiceGrpc.AdServiceBlockingStub stub = AdServiceGrpc.newBlockingStub(channel);
              }
            }
            """);
        // 提交进仓库的 protoc 生成代码：里面的 newBlockingStub 是定义，不是调用
        Write("ads/src/main/java/ads/AdServiceGrpc.java", """
            package ads;

            @io.grpc.stub.annotations.GrpcGenerated
            public final class AdServiceGrpc {
              public static AdServiceBlockingStub newBlockingStub(io.grpc.Channel channel) { return CartServiceGrpc.newBlockingStub(channel); }
            }
            """);

        // ── 同目录 pom.xml 与 build.gradle：Maven 优先 ──
        Write("clinic/pom.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <groupId>org.acme</groupId>
              <artifactId>clinic</artifactId>
              <dependencies>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-webmvc</artifactId></dependency>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-data-jpa</artifactId></dependency>
                <dependency><groupId>com.h2database</groupId><artifactId>h2</artifactId><scope>runtime</scope></dependency>
                <dependency><groupId>com.mysql</groupId><artifactId>mysql-connector-j</artifactId><scope>runtime</scope></dependency>
              </dependencies>
            </project>
            """);
        Write("clinic/build.gradle", "dependencies {\n    runtimeOnly 'org.mariadb.jdbc:mariadb-java-client'\n}\n");
        Write("clinic/src/main/resources/application.properties", "database=h2\nspring.sql.init.schema-locations=classpath*:db/${database}/schema.sql\n");
        Write("clinic/src/main/resources/application-postgres.properties", "spring.datasource.url=${POSTGRES_URL:jdbc:postgresql://localhost/petclinic}\n");
        Write("clinic/src/main/java/org/acme/clinic/ClinicApplication.java", """
            package org.acme.clinic;

            @SpringBootApplication
            public class ClinicApplication {
                public static void main(String[] args) {
                    SpringApplication.run(ClinicApplication.class, args);
                }
            }
            """);
    }

    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
