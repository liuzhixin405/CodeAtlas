using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 框架流程 · Spring（SpringFrameworkFlow）：Spring MVC + Security 夹具（过滤器顺序、Security 链与 addFilterBefore、FilterRegistrationBean、拦截器书写顺序与 .order、
/// 切面与 @Pointcut、@Transactional、@ControllerAdvice / ResponseEntityExceptionHandler、ResponseBodyAdvice）、WebFlux + Gateway 夹具（WebFilter、GlobalFilter、
/// default-filters 与自研过滤器工厂、路由）、非 Web 容器、顺序不确定的变体、框架切面与缓存代理、配置解析。夹具里的调用位置按源码文本算，与真实索引一致。
/// </summary>
public class SpringFrameworkFlowTests
{
    // ================= 夹具工具 =================

    /// <summary>一个假仓库：源码文本、符号（行号从文本里找）、调用（位置从文本里找）、签名。</summary>
    private sealed class Repo
    {
        public readonly List<MapSymbol> Symbols = [];
        public readonly List<MapEdge> Edges = [];
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
        public readonly Dictionary<long, IReadOnlyList<FrameworkCall>> Calls = new();
        public readonly Dictionary<long, string> Signatures = new();
        public readonly List<EntryPoint> Entries = [];
        private long next = 1;
        private long ext = 9000;

        public string File(string path, string text) { Files[path] = text.Replace("\r\n", "\n"); return path; }

        private static int LineOf(string text, int pos) => text[..pos].Count(c => c == '\n') + 1;

        private int Index(string path, string needle, int nth = 0)
        {
            var text = Files[path];
            var i = -1;
            for (var k = 0; k <= nth; k++)
            {
                i = text.IndexOf(needle, i + 1, StringComparison.Ordinal);
                Assert.True(i >= 0, $"{path} 里没有 {needle}");
            }
            return i;
        }

        /// <summary>类型：声明行 = needle 所在行，结束行 = 声明后第一个 { 配对的 }。</summary>
        public long Type(string path, string qn, string needle, SymbolKind kind = SymbolKind.Class)
        {
            var (start, end) = Lines(path, needle);
            var id = next++;
            Symbols.Add(new MapSymbol(id, kind, qn, path, null, start, end));
            return id;
        }

        public long Method(long owner, string path, string qn, string needle, string? signature = null)
        {
            var (start, end) = Lines(path, needle);
            var id = next++;
            Symbols.Add(new MapSymbol(id, SymbolKind.Method, qn, path, owner, start, end));
            if (signature is not null) Signatures[id] = signature;
            return id;
        }

        private (int Start, int End) Lines(string path, string needle)
        {
            var text = Files[path];
            var at = Index(path, needle);
            var brace = text.IndexOf('{', at);
            var depth = 0;
            var close = text.Length - 1;
            for (var i = brace; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) { close = i; break; }
            }
            return (LineOf(text, at), LineOf(text, close));
        }

        /// <summary>一次调用：位置 = needle 在文件里第 nth 次出现处。</summary>
        public FrameworkCall Call(string path, string needle, long? target, string label, string qn, string kind = "call", string? ctx = null, int nth = 0)
        {
            var pos = Index(path, needle, nth);
            return new FrameworkCall(target, kind, label, qn, LineOf(Files[path], pos), pos, ctx, kind == "call", []);
        }

        /// <summary>外部调用：限定名 external:类型.方法（tree-sitter 给 Java 的样子）。</summary>
        public FrameworkCall Ext(string path, string needle, string typeDotMember, string? ctx = null, int nth = 0) =>
            Call(path, needle, ext++, typeDotMember, "external:" + typeDotMember, "external", ctx, nth);

        public void Body(long method, params FrameworkCall[] calls) => Calls[method] = calls;

        public void Implements(long from, string externalName)
        {
            var id = next++;
            Symbols.Add(new MapSymbol(id, SymbolKind.External, "external:" + externalName, "<external>", null));
            Edges.Add(new MapEdge(from, id, EdgeKind.Implements));
        }

        public FrameworkInput Input(string key, params string[] members) => new(
            Symbols, Edges, new ArchNode(key, ArchNodeKind.Container, "java-service", key, members[0], ArchSource.Discovered, [], members, []), Entries, [],
            new FrameworkSources
            {
                Calls = id => Calls.GetValueOrDefault(id) ?? [],
                Signature = id => Signatures.GetValueOrDefault(id),
                ReadFile = p => Files.GetValueOrDefault(p),
            });
    }

    private static List<FlowNodeDto> All(FlowNodeDto n) => [n, .. n.Children.SelectMany(All)];

    private static IEnumerable<string> Labels(FlowNodeDto n) => n.Children.Select(c => c.Label);

    // ================= 夹具一：Spring MVC + Security =================

    private const string Pom = """
        <project>
          <artifactId>shop-api</artifactId>
          <dependencies>
            <dependency><artifactId>spring-boot-starter-web</artifactId></dependency>
            <dependency><artifactId>spring-boot-starter-security</artifactId></dependency>
            <dependency><artifactId>spring-boot-starter-test</artifactId></dependency>
          </dependencies>
        </project>
        """;

    private const string J = "shop-api/src/main/java/com/acme/shop/";

    private static Repo MvcFixture()
    {
        var r = new Repo();
        r.File("shop-api/pom.xml", Pom);

        // ---- 过滤器 ----
        var requestId = r.File(J + "web/RequestIdFilter.java", """
            package com.acme.shop.web;

            @Component
            @Order(1)
            public class RequestIdFilter extends OncePerRequestFilter {
                @Override
                protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain chain) throws ServletException, IOException {
                    MdcSupport.put(request.getHeader("X-Request-Id"));
                    chain.doFilter(request, response);
                    MdcSupport.clear();
                }
            }
            """);
        var mdc = r.File(J + "web/MdcSupport.java", """
            public class MdcSupport {
                public static void put(String id) { }
                public static void clear() { }
            }
            """);
        var mdcType = r.Type(mdc, "com.acme.shop.web.MdcSupport", "public class MdcSupport");
        var put = r.Method(mdcType, mdc, "com.acme.shop.web.MdcSupport.put(String)", "public static void put");
        var clear = r.Method(mdcType, mdc, "com.acme.shop.web.MdcSupport.clear()", "public static void clear");
        var ridType = r.Type(requestId, "com.acme.shop.web.RequestIdFilter", "public class RequestIdFilter");
        var ridMain = r.Method(ridType, requestId, "com.acme.shop.web.RequestIdFilter.doFilterInternal(HttpServletRequest,HttpServletResponse,FilterChain)", "protected void doFilterInternal",
            "protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain chain) throws ServletException, IOException");
        r.Body(ridMain,
            r.Call(requestId, "MdcSupport.put", put, "MdcSupport.put", "com.acme.shop.web.MdcSupport.put(String)"),
            r.Ext(requestId, "request.getHeader", "HttpServletRequest.getHeader"),
            r.Ext(requestId, "chain.doFilter", "FilterChain.doFilter"),
            r.Call(requestId, "MdcSupport.clear", clear, "MdcSupport.clear", "com.acme.shop.web.MdcSupport.clear()"));

        var audit = r.File(J + "web/AuditFilter.java", """
            package com.acme.shop.web;

            public class AuditFilter implements javax.servlet.Filter {
                @Override
                public void doFilter(ServletRequest req, ServletResponse res, FilterChain chain) throws IOException, ServletException {
                    if (AuditLog.skip(req)) {
                        chain.doFilter(req, res);
                        return;
                    }
                    AuditLog.start(req);
                    chain.doFilter(req, res);
                    AuditLog.finish(res);
                }
            }
            """);
        var auditLog = r.File(J + "web/AuditLog.java", """
            public class AuditLog {
                public static boolean skip(Object o) { return false; }
                public static void start(Object o) { }
                public static void finish(Object o) { }
                public static void flush() { }
            }
            """);
        var logType = r.Type(auditLog, "com.acme.shop.web.AuditLog", "public class AuditLog");
        var skip = r.Method(logType, auditLog, "com.acme.shop.web.AuditLog.skip(Object)", "public static boolean skip");
        var start = r.Method(logType, auditLog, "com.acme.shop.web.AuditLog.start(Object)", "public static void start");
        var finish = r.Method(logType, auditLog, "com.acme.shop.web.AuditLog.finish(Object)", "public static void finish");
        var flush = r.Method(logType, auditLog, "com.acme.shop.web.AuditLog.flush()", "public static void flush");
        var auditType = r.Type(audit, "com.acme.shop.web.AuditFilter", "public class AuditFilter");
        var auditMain = r.Method(auditType, audit, "com.acme.shop.web.AuditFilter.doFilter(ServletRequest,ServletResponse,FilterChain)", "public void doFilter",
            "public void doFilter(ServletRequest req, ServletResponse res, FilterChain chain) throws IOException, ServletException");
        r.Body(auditMain,
            r.Call(audit, "AuditLog.skip", skip, "AuditLog.skip", "com.acme.shop.web.AuditLog.skip(Object)"),
            r.Ext(audit, "chain.doFilter", "FilterChain.doFilter", CallContext.Cond),
            r.Call(audit, "AuditLog.start", start, "AuditLog.start", "com.acme.shop.web.AuditLog.start(Object)"),
            r.Ext(audit, "chain.doFilter", "FilterChain.doFilter", nth: 1),
            r.Call(audit, "AuditLog.finish", finish, "AuditLog.finish", "com.acme.shop.web.AuditLog.finish(Object)"));

        // JWT 过滤器：调用大纲里没有 filterChain.doFilter（没解析到的不进大纲），靠源码文本切分
        var jwt = r.File(J + "security/JwtAuthFilter.java", """
            package com.acme.shop.security;

            public class JwtAuthFilter extends OncePerRequestFilter {
                @Autowired private TokenService tokenService;

                @Override
                protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain filterChain) throws ServletException, IOException {
                    tokenService.verify(request);
                    filterChain.doFilter(request, response);
                    SecurityAudit.record(request);
                }
            }
            """);
        var jwtType = r.Type(jwt, "com.acme.shop.security.JwtAuthFilter", "public class JwtAuthFilter");
        var jwtMain = r.Method(jwtType, jwt, "com.acme.shop.security.JwtAuthFilter.doFilterInternal(HttpServletRequest,HttpServletResponse,FilterChain)", "protected void doFilterInternal",
            "protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain filterChain) throws ServletException, IOException");
        r.Body(jwtMain,
            r.Call(jwt, "tokenService.verify", 900, "TokenService.verify", "com.acme.shop.security.TokenService.verify(HttpServletRequest)"),
            r.Call(jwt, "SecurityAudit.record", 901, "SecurityAudit.record", "com.acme.shop.security.SecurityAudit.record(HttpServletRequest)"));
        r.Symbols.Add(new MapSymbol(900, SymbolKind.Method, "com.acme.shop.security.TokenService.verify(HttpServletRequest)", J + "security/TokenService.java", null, 3, 5));
        r.Symbols.Add(new MapSymbol(901, SymbolKind.Method, "com.acme.shop.security.SecurityAudit.record(HttpServletRequest)", J + "security/SecurityAudit.java", null, 3, 5));

        // Security：@Bean 返回 JwtAuthFilter，又 addFilterBefore 进 Security 链（realworld 的写法）
        var security = r.File(J + "security/SecurityConfig.java", """
            package com.acme.shop.security;

            @Configuration
            @EnableWebSecurity
            public class SecurityConfig {
                @Bean
                public JwtAuthFilter jwtAuthFilter() {
                    return new JwtAuthFilter();
                }

                @Bean
                public SecurityFilterChain filterChain(HttpSecurity http) throws Exception {
                    http.csrf(c -> c.disable())
                        .sessionManagement(s -> s.sessionCreationPolicy(SessionCreationPolicy.STATELESS))
                        .authorizeHttpRequests(a -> a.anyRequest().authenticated());
                    http.addFilterBefore(jwtAuthFilter(), UsernamePasswordAuthenticationFilter.class);
                    // http.addFilterAfter(new DebugFilter(), JwtAuthFilter.class);
                    return http.build();
                }
            }
            """);
        var secType = r.Type(security, "com.acme.shop.security.SecurityConfig", "public class SecurityConfig");
        r.Method(secType, security, "com.acme.shop.security.SecurityConfig.jwtAuthFilter()", "public JwtAuthFilter jwtAuthFilter", "public JwtAuthFilter jwtAuthFilter()");
        r.Method(secType, security, "com.acme.shop.security.SecurityConfig.filterChain(HttpSecurity)", "public SecurityFilterChain filterChain", "public SecurityFilterChain filterChain(HttpSecurity http) throws Exception");

        // FilterRegistrationBean 注册 AuditFilter；@Bean 返回框架的 CorsFilter
        var filterConfig = r.File(J + "web/FilterConfig.java", """
            package com.acme.shop.web;

            @Configuration
            public class FilterConfig {
                @Bean
                public FilterRegistrationBean<AuditFilter> auditFilter() {
                    FilterRegistrationBean<AuditFilter> reg = new FilterRegistrationBean<>();
                    reg.setFilter(new AuditFilter());
                    reg.setOrder(Ordered.HIGHEST_PRECEDENCE + 10);
                    reg.addUrlPatterns("/api/*");
                    return reg;
                }

                @Bean
                public CorsFilter corsFilter() {
                    return new CorsFilter(new UrlBasedCorsConfigurationSource());
                }
            }
            """);
        var fcType = r.Type(filterConfig, "com.acme.shop.web.FilterConfig", "public class FilterConfig");
        r.Method(fcType, filterConfig, "com.acme.shop.web.FilterConfig.auditFilter()", "public FilterRegistrationBean<AuditFilter> auditFilter", "public FilterRegistrationBean<AuditFilter> auditFilter()");
        r.Method(fcType, filterConfig, "com.acme.shop.web.FilterConfig.corsFilter()", "public CorsFilter corsFilter", "public CorsFilter corsFilter()");

        // ---- 拦截器 ----
        var webConfig = r.File(J + "web/WebConfig.java", """
            package com.acme.shop.web;

            @Configuration
            public class WebConfig implements WebMvcConfigurer {
                @Autowired
                private TenantInterceptor tenantInterceptor;

                @Bean
                public LocaleChangeInterceptor localeChangeInterceptor() {
                    return new LocaleChangeInterceptor();
                }

                @Override
                public void addInterceptors(InterceptorRegistry registry) {
                    // registry.addInterceptor(new DebugInterceptor());
                    registry.addInterceptor(new AuthInterceptor()).addPathPatterns("/api/**").excludePathPatterns("/api/login");
                    registry.addInterceptor(localeChangeInterceptor());
                    registry.addInterceptor(tenantInterceptor).order(-1);
                }
            }
            """);
        var wcType = r.Type(webConfig, "com.acme.shop.web.WebConfig", "public class WebConfig");
        r.Implements(wcType, "WebMvcConfigurer");
        r.Method(wcType, webConfig, "com.acme.shop.web.WebConfig.localeChangeInterceptor()", "public LocaleChangeInterceptor localeChangeInterceptor", "public LocaleChangeInterceptor localeChangeInterceptor()");
        r.Method(wcType, webConfig, "com.acme.shop.web.WebConfig.addInterceptors(InterceptorRegistry)", "public void addInterceptors", "public void addInterceptors(InterceptorRegistry registry)");

        var auth = r.File(J + "web/AuthInterceptor.java", """
            package com.acme.shop.web;

            public class AuthInterceptor implements HandlerInterceptor {
                @Override
                public boolean preHandle(HttpServletRequest request, HttpServletResponse response, Object handler) {
                    if (!SessionStore.check(request)) {
                        return false;
                    }
                    return true;
                }

                @Override
                public void postHandle(HttpServletRequest request, HttpServletResponse response, Object handler, ModelAndView mv) {
                    Metrics.record(request);
                }

                @Override
                public void afterCompletion(HttpServletRequest request, HttpServletResponse response, Object handler, Exception ex) {
                    AuditLog.flush();
                }
            }
            """);
        var authType = r.Type(auth, "com.acme.shop.web.AuthInterceptor", "public class AuthInterceptor");
        r.Implements(authType, "HandlerInterceptor");
        var authPre = r.Method(authType, auth, "com.acme.shop.web.AuthInterceptor.preHandle(HttpServletRequest,HttpServletResponse,Object)", "public boolean preHandle");
        var authPost = r.Method(authType, auth, "com.acme.shop.web.AuthInterceptor.postHandle(HttpServletRequest,HttpServletResponse,Object,ModelAndView)", "public void postHandle");
        var authAfter = r.Method(authType, auth, "com.acme.shop.web.AuthInterceptor.afterCompletion(HttpServletRequest,HttpServletResponse,Object,Exception)", "public void afterCompletion");
        r.Body(authPre, r.Call(auth, "SessionStore.check", 902, "SessionStore.check", "com.acme.shop.web.SessionStore.check(HttpServletRequest)", ctx: CallContext.Cond));
        r.Body(authPost, r.Call(auth, "Metrics.record", 903, "Metrics.record", "com.acme.shop.web.Metrics.record(HttpServletRequest)"));
        r.Body(authAfter, r.Call(auth, "AuditLog.flush", flush, "AuditLog.flush", "com.acme.shop.web.AuditLog.flush()"));

        var tenant = r.File(J + "web/TenantInterceptor.java", """
            package com.acme.shop.web;

            @Component
            public class TenantInterceptor implements HandlerInterceptor {
                @Override
                public boolean preHandle(HttpServletRequest request, HttpServletResponse response, Object handler) {
                    TenantContext.set(request.getHeader("X-Tenant"));
                    return true;
                }

                @Override
                public void postHandle(HttpServletRequest request, HttpServletResponse response, Object handler, ModelAndView mv) {
                    TenantContext.stamp(response);
                }

                @Override
                public void afterCompletion(HttpServletRequest request, HttpServletResponse response, Object handler, Exception ex) {
                    TenantContext.clear();
                }
            }
            """);
        var tenantType = r.Type(tenant, "com.acme.shop.web.TenantInterceptor", "public class TenantInterceptor");
        var tenantPre = r.Method(tenantType, tenant, "com.acme.shop.web.TenantInterceptor.preHandle(HttpServletRequest,HttpServletResponse,Object)", "public boolean preHandle");
        var tenantPost = r.Method(tenantType, tenant, "com.acme.shop.web.TenantInterceptor.postHandle(HttpServletRequest,HttpServletResponse,Object,ModelAndView)", "public void postHandle");
        r.Body(tenantPost, r.Call(tenant, "TenantContext.stamp", 911, "TenantContext.stamp", "com.acme.shop.web.TenantContext.stamp(HttpServletResponse)"));
        var tenantAfter = r.Method(tenantType, tenant, "com.acme.shop.web.TenantInterceptor.afterCompletion(HttpServletRequest,HttpServletResponse,Object,Exception)", "public void afterCompletion");
        r.Body(tenantPre, r.Call(tenant, "TenantContext.set", 904, "TenantContext.set", "com.acme.shop.web.TenantContext.set(String)"), r.Ext(tenant, "request.getHeader", "HttpServletRequest.getHeader"));
        r.Body(tenantAfter, r.Call(tenant, "TenantContext.clear", 905, "TenantContext.clear", "com.acme.shop.web.TenantContext.clear()"));

        // ---- 切面 ----
        var logging = r.File(J + "aop/LoggingAspect.java", """
            package com.acme.shop.aop;

            @Aspect
            @Component
            @Order(2)
            public class LoggingAspect {
                private static final Logger log = LoggerFactory.getLogger(LoggingAspect.class);

                @Pointcut("execution(* com.acme.shop.service..*(..))")
                public void serviceLayer() {
                }

                @Around("serviceLayer()")
                public Object around(ProceedingJoinPoint pjp) throws Throwable {
                    log.info("enter");
                    Object result = pjp.proceed();
                    log.info("exit");
                    return result;
                }

                @AfterThrowing(pointcut = "serviceLayer()", throwing = "ex")
                public void onError(Throwable ex) {
                    ErrorReporter.report(ex);
                }
            }
            """);
        var logAspect = r.Type(logging, "com.acme.shop.aop.LoggingAspect", "public class LoggingAspect");
        r.Method(logAspect, logging, "com.acme.shop.aop.LoggingAspect.serviceLayer()", "public void serviceLayer");
        var around = r.Method(logAspect, logging, "com.acme.shop.aop.LoggingAspect.around(ProceedingJoinPoint)", "public Object around", "public Object around(ProceedingJoinPoint pjp) throws Throwable");
        var onError = r.Method(logAspect, logging, "com.acme.shop.aop.LoggingAspect.onError(Throwable)", "public void onError", "public void onError(Throwable ex)");
        r.Body(around, r.Ext(logging, "log.info", "Logger.info"), r.Ext(logging, "pjp.proceed", "ProceedingJoinPoint.proceed"), r.Ext(logging, "log.info", "Logger.info", nth: 1));
        r.Body(onError, r.Call(logging, "ErrorReporter.report", 906, "ErrorReporter.report", "com.acme.shop.aop.ErrorReporter.report(Throwable)"));

        var metrics = r.File(J + "aop/MetricsAspect.java", """
            package com.acme.shop.aop;

            @Aspect
            @Component
            @Order(1)
            public class MetricsAspect {
                @Before("@annotation(com.acme.shop.Timed)")
                public void mark(JoinPoint jp) {
                    Metrics.mark(jp);
                }

                @After("@annotation(com.acme.shop.Timed)")
                public void done(JoinPoint jp) {
                    Metrics.done(jp);
                }
            }
            """);
        var metricsAspect = r.Type(metrics, "com.acme.shop.aop.MetricsAspect", "public class MetricsAspect");
        var mark = r.Method(metricsAspect, metrics, "com.acme.shop.aop.MetricsAspect.mark(JoinPoint)", "public void mark");
        var done = r.Method(metricsAspect, metrics, "com.acme.shop.aop.MetricsAspect.done(JoinPoint)", "public void done");
        r.Body(mark, r.Call(metrics, "Metrics.mark", 907, "Metrics.mark", "com.acme.shop.web.Metrics.mark(JoinPoint)"));
        r.Body(done, r.Call(metrics, "Metrics.done", 908, "Metrics.done", "com.acme.shop.web.Metrics.done(JoinPoint)"));

        // ---- 事务 ----
        var orderService = r.File(J + "service/OrderService.java", """
            package com.acme.shop.service;

            @Service
            @Transactional
            public class OrderService {
                public void place() { }
            }
            """);
        r.Type(orderService, "com.acme.shop.service.OrderService", "public class OrderService");
        var payment = r.File(J + "service/PaymentService.java", """
            package com.acme.shop.service;

            @Service
            public class PaymentService {
                @Transactional(readOnly = true)
                public void history() { }
            }
            """);
        var payType = r.Type(payment, "com.acme.shop.service.PaymentService", "public class PaymentService");
        r.Method(payType, payment, "com.acme.shop.service.PaymentService.history()", "public void history");

        // ---- 控制器、异常处理、响应体改写 ----
        var controller = r.File(J + "api/OrderController.java", """
            package com.acme.shop.api;

            @RestController
            public class OrderController {
                @PostMapping("/api/orders")
                public Order create(@RequestBody Order order) { return order; }

                @GetMapping("/api/orders/{id}")
                public Order get(@PathVariable long id) { return null; }

                @ExceptionHandler(OrderConflictException.class)
                public ResponseEntity<String> handleConflict(OrderConflictException e) {
                    return ErrorBody.conflict(e);
                }
            }
            """);
        var ctrlType = r.Type(controller, "com.acme.shop.api.OrderController", "public class OrderController");
        var create = r.Method(ctrlType, controller, "com.acme.shop.api.OrderController.create(Order)", "public Order create");
        var get = r.Method(ctrlType, controller, "com.acme.shop.api.OrderController.get(long)", "public Order get");
        r.Method(ctrlType, controller, "com.acme.shop.api.OrderController.handleConflict(OrderConflictException)", "public ResponseEntity<String> handleConflict");
        r.Entries.Add(new EntryPoint(create, "shop-api", "spring.mvc", "http", "/api/orders", "POST", controller, 6));
        r.Entries.Add(new EntryPoint(get, "shop-api", "spring.mvc", "http", "/api/orders/{id}", "GET", controller, 9));
        r.Entries.Add(new EntryPoint(ctrlType, "shop-api", "spring.boot.main", "hosted", null, null, controller, 4));

        var advice = r.File(J + "api/GlobalExceptionHandler.java", """
            package com.acme.shop.api;

            @RestControllerAdvice(basePackages = "com.acme.shop.api")
            public class GlobalExceptionHandler extends ResponseEntityExceptionHandler {
                @ExceptionHandler(OrderNotFoundException.class)
                public ResponseEntity<ErrorBody> handleNotFound(OrderNotFoundException e) {
                    return ErrorBody.of(e);
                }

                @ExceptionHandler({IllegalStateException.class, IllegalArgumentException.class})
                public ResponseEntity<ErrorBody> handleBadState(RuntimeException e) {
                    return ErrorBody.of(e);
                }

                @Override
                protected ResponseEntity<Object> handleMethodArgumentNotValid(MethodArgumentNotValidException ex, HttpHeaders headers, HttpStatus status, WebRequest request) {
                    return ErrorBody.invalid(ex);
                }
            }
            """);
        var adviceType = r.Type(advice, "com.acme.shop.api.GlobalExceptionHandler", "public class GlobalExceptionHandler");
        var notFound = r.Method(adviceType, advice, "com.acme.shop.api.GlobalExceptionHandler.handleNotFound(OrderNotFoundException)", "public ResponseEntity<ErrorBody> handleNotFound");
        r.Method(adviceType, advice, "com.acme.shop.api.GlobalExceptionHandler.handleBadState(RuntimeException)", "public ResponseEntity<ErrorBody> handleBadState", "public ResponseEntity<ErrorBody> handleBadState(RuntimeException e)");
        r.Method(adviceType, advice, "com.acme.shop.api.GlobalExceptionHandler.handleMethodArgumentNotValid(MethodArgumentNotValidException,HttpHeaders,HttpStatus,WebRequest)", "protected ResponseEntity<Object> handleMethodArgumentNotValid",
            "protected ResponseEntity<Object> handleMethodArgumentNotValid(MethodArgumentNotValidException ex, HttpHeaders headers, HttpStatus status, WebRequest request)");
        r.Body(notFound, r.Call(advice, "ErrorBody.of", 909, "ErrorBody.of", "com.acme.shop.api.ErrorBody.of(Exception)"));

        var bodyAdvice = r.File(J + "api/ApiResponseAdvice.java", """
            package com.acme.shop.api;

            @RestControllerAdvice
            public class ApiResponseAdvice implements ResponseBodyAdvice<Object> {
                @Override
                public Object beforeBodyWrite(Object body, MethodParameter p, MediaType t, Class c, ServerHttpRequest req, ServerHttpResponse res) {
                    return ApiResponse.wrap(body);
                }
            }
            """);
        var bodyType = r.Type(bodyAdvice, "com.acme.shop.api.ApiResponseAdvice", "public class ApiResponseAdvice");
        var write = r.Method(bodyType, bodyAdvice, "com.acme.shop.api.ApiResponseAdvice.beforeBodyWrite(Object,MethodParameter,MediaType,Class,ServerHttpRequest,ServerHttpResponse)", "public Object beforeBodyWrite");
        r.Body(write, r.Call(bodyAdvice, "ApiResponse.wrap", 910, "ApiResponse.wrap", "com.acme.shop.api.ApiResponse.wrap(Object)"));
        return r;
    }

    [Fact]
    public void Mvc_StagesAndFilterOrder_SecurityChainWithAddFilterBefore()
    {
        var f = FrameworkFlowBuilder.Build(MvcFixture().Input("shop-api", "shop-api"));

        Assert.Equal(["middleware", "dispatcher", "interceptor", "controller", "aspect", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["过滤器", "分派器", "拦截器", "控制器", "切面", "收尾"], f.Stages.Select(s => s.Label));
        Assert.All(f.Stages, s => Assert.True(s.OrderKnown, s.Key));
        Assert.Equal(f.Stages.Select(s => s.Key), f.Flow.Actors.Select(a => a.Key));
        Assert.Empty(f.Handlers);

        var root = f.Flow.Root;
        Assert.Equal(("HTTP 请求", "entry", "middleware"), (root.Label, root.Kind, root.Actor));
        // 顺序：AuditFilter（HIGHEST + 10）→ Security 链（-100）→ RequestIdFilter（@Order(1)）→ CorsFilter（@Bean，没写顺序）→ 分派器 → 后置倒序
        Assert.Equal(["AuditFilter.doFilter", "Spring Security 过滤器链", "RequestIdFilter.doFilterInternal", "CorsFilter", "DispatcherServlet（Spring 内置）",
                      "MdcSupport.clear", "SecurityAudit.record", "AuditLog.finish"], Labels(root));

        var auditNode = root.Children[0];
        Assert.Equal(("call", "middleware"), (auditNode.Kind, auditNode.Actor));
        Assert.Equal(["AuditLog.skip", "AuditLog.start"], Labels(auditNode));       // 分支里的 chain.doFilter 不是切分点
        Assert.Contains("FilterRegistrationBean（FilterConfig.java:6）", auditNode.Note);
        Assert.Contains("路径 /api/*", auditNode.Note);
        Assert.Contains("有条件", auditNode.Note);
        Assert.Contains("FilterRegistrationBean.setOrder(HIGHEST_PRECEDENCE + 10)", auditNode.Note);
        Assert.Equal((J + "web/FilterConfig.java", 6), (auditNode.Path, auditNode.Line));

        var security = root.Children[1];
        Assert.Equal("external-group", security.Kind);
        Assert.Contains("SecurityFilterChain filterChain()（SecurityConfig.java:12）", security.Note);
        Assert.Contains("配置了 csrf（关闭）、sessionManagement、authorizeHttpRequests", security.Note);
        var jwt = Assert.Single(security.Children);                                   // 注释里的 addFilterAfter 不算
        Assert.Equal(("JwtAuthFilter.doFilterInternal", "middleware"), (jwt.Label, jwt.Actor));
        Assert.Equal(["TokenService.verify"], Labels(jwt));                            // 源码文本里找到的 filterChain.doFilter 之前
        Assert.StartsWith("addFilterBefore(UsernamePasswordAuthenticationFilter)（SecurityConfig.java:16）", jwt.Note);
        Assert.Contains("同时是 @Bean jwtAuthFilter()", jwt.Note);
        Assert.Contains("OncePerRequestFilter 会跳过第二次", jwt.Note);
        Assert.DoesNotContain(root.Children, c => c.Label == "JwtAuthFilter.doFilterInternal"); // 只画在链里

        var rid = root.Children[2];
        Assert.Equal(["MdcSupport.put", "HttpServletRequest.getHeader"], Labels(rid));
        Assert.Contains("@Component", rid.Note);
        Assert.Contains("顺序 @Order(1)", rid.Note);
        Assert.Contains("框架 / 第三方过滤器，@Bean corsFilter()", root.Children[3].Note);

        Assert.Equal(["RequestIdFilter 后置", "JwtAuthFilter 后置", "AuditFilter 后置"], root.Children.Skip(5).Select(c => c.Note));
        Assert.All(root.Children.Skip(5), c => Assert.Equal("tail", c.Actor));

        Assert.All(All(root), n => Assert.All(n.Children, c => Assert.Equal(n.Depth + 1, c.Depth)));
        Assert.Equal(All(root).Count, f.Flow.Nodes);
        Assert.Contains(f.Notes, n => n.StartsWith("过滤器按 FilterRegistrationBean.setOrder", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("另有 1 个非 HTTP 入口"));
        // 阶段的符号按骨架里的行数：外部的（没有符号）也算一条、Id = 0，阶段头上的个数才和下面列的行数一致；仓库里的照旧按顺序
        Assert.Equal(["AuditFilter", "JwtAuthFilter", "RequestIdFilter"], f.Stages[0].Symbols.Where(s => s.Id != 0).Select(s => s.QualifiedName.Split('(')[0].Split('.')[^2]));
    }

    [Fact]
    public void Mvc_InterceptorsByOrderThenWriting_ControllerChildren_TailOrder()
    {
        var f = FrameworkFlowBuilder.Build(MvcFixture().Input("shop-api", "shop-api"));
        var dispatcher = f.Flow.Root.Children.Single(c => c.Actor == "dispatcher");
        Assert.Equal(("external-group", 1), (dispatcher.Kind, dispatcher.Depth));
        // 拦截器：.order(-1) 的 Tenant 先，Auth 与 Locale 同为 0 按书写顺序；出来：postHandle 倒序 → 异常处理（控制器内的先）→ afterCompletion 倒序
        Assert.Equal(
            ["TenantInterceptor.preHandle", "AuthInterceptor.preHandle", "LocaleChangeInterceptor", "控制器",
             "AuthInterceptor.postHandle", "TenantInterceptor.postHandle",
             "OrderController.handleConflict", "GlobalExceptionHandler.handleNotFound", "GlobalExceptionHandler.handleBadState", "GlobalExceptionHandler : ResponseEntityExceptionHandler",
             "AuthInterceptor.afterCompletion", "TenantInterceptor.afterCompletion"],
            Labels(dispatcher));
        var tenant = dispatcher.Children[0];
        Assert.Equal(["TenantContext.set", "HttpServletRequest.getHeader"], Labels(tenant));
        Assert.Contains("order(-1)", tenant.Note);
        var auth = dispatcher.Children[1];
        Assert.Equal(("interceptor", "call"), (auth.Actor, auth.Kind));
        Assert.Equal(["SessionStore.check"], Labels(auth));
        Assert.Contains("registry.addInterceptor（WebConfig.java:16）", auth.Note);
        Assert.Contains("路径 /api/**", auth.Note);
        Assert.Contains("排除 /api/login", auth.Note);
        Assert.Contains("返回 false", auth.Note);
        Assert.Equal((J + "web/WebConfig.java", 16), (auth.Path, auth.Line));
        var locale = dispatcher.Children[2];
        Assert.Equal("external-group", locale.Kind);
        Assert.Contains("Spring / 第三方内置", locale.Note);

        Assert.Equal(["Metrics.record"], Labels(dispatcher.Children[4]));
        Assert.Equal("tail", dispatcher.Children[4].Actor);
        Assert.Equal(["TenantContext.stamp"], Labels(dispatcher.Children[5]));
        Assert.Contains("出错时：OrderConflictException（控制器内，只管 OrderController）", dispatcher.Children[6].Note);
        Assert.Contains("出错时：OrderNotFoundException（@RestControllerAdvice，范围 basePackages = \"com.acme.shop.api\"）", dispatcher.Children[7].Note);
        Assert.Equal(["ErrorBody.of"], Labels(dispatcher.Children[7]));
        Assert.Contains("IllegalStateException、IllegalArgumentException", dispatcher.Children[8].Note);
        var baseHandler = dispatcher.Children[9];
        Assert.Equal("external-group", baseHandler.Kind);
        Assert.Equal(["GlobalExceptionHandler.handleMethodArgumentNotValid"], Labels(baseHandler));
        Assert.Contains("MethodArgumentNotValidException", baseHandler.Children[0].Note);

        // 控制器：切面进入（@Order(1) 的 Metrics 在外）→ 事务 → 切面出来（Logging 先出：AfterThrowing → Around 后置；再 Metrics 的 @After）→ 响应体改写
        var controllers = dispatcher.Children[3];
        Assert.Equal(("controllers", 2, "controller"), (controllers.Kind, controllers.Count, controllers.Actor));
        Assert.Equal(["MetricsAspect.mark", "LoggingAspect.around", "@Transactional 提交 / 回滚", "LoggingAspect.onError", "Logger.info", "MetricsAspect.done", "ApiResponseAdvice.beforeBodyWrite"],
            Labels(controllers));
        Assert.Equal(["aspect", "aspect", "tail", "tail", "tail", "tail", "tail"], controllers.Children.Select(c => c.Actor));
        var aroundNode = controllers.Children[1];
        Assert.Equal(["Logger.info"], Labels(aroundNode));                              // proceed 之前
        Assert.Contains("切点 serviceLayer() = execution(* com.acme.shop.service..*(..))", aroundNode.Note);
        Assert.Contains("顺序 @Order(2)", aroundNode.Note);
        Assert.Contains("@Before；切点 @annotation(com.acme.shop.Timed)", controllers.Children[0].Note);
        Assert.Contains("类 1、方法 1", controllers.Children[2].Note);
        Assert.Contains("readOnly 1 处", controllers.Children[2].Note);
        Assert.Contains("@AfterThrowing（出错时）", controllers.Children[3].Note);
        Assert.Equal("LoggingAspect 后置（proceed 之后）", controllers.Children[4].Note);
        Assert.Contains("@After（无论成败）", controllers.Children[5].Note);

        Assert.Contains(f.Notes, n => n.StartsWith("拦截器按 WebConfig.addInterceptors（WebConfig.java:14）", StringComparison.Ordinal) && n.Contains(".order(n)"));
        Assert.Contains(f.Notes, n => n.Contains("LocaleChangeInterceptor 是 Spring / 第三方的拦截器"));
        Assert.Contains(f.Notes, n => n.StartsWith("切面：2 个 @Aspect 类", StringComparison.Ordinal));
        Assert.Contains(f.Stages.Single(s => s.Key == "tail").Symbols, s => s.QualifiedName.EndsWith("AuthInterceptor.afterCompletion(HttpServletRequest,HttpServletResponse,Object,Exception)", StringComparison.Ordinal));
    }

    [Fact]
    public void Mvc_UnregisteredAndTiedOrders_MarkOrderUnknown()
    {
        var r = MvcFixture();
        var legacy = r.File(J + "web/LegacyFilter.java", """
            public class LegacyFilter implements Filter {
                public void doFilter(ServletRequest req, ServletResponse res, FilterChain chain) {
                    chain.doFilter(req, res);
                }
            }
            """);
        var lt = r.Type(legacy, "com.acme.shop.web.LegacyFilter", "public class LegacyFilter");
        r.Method(lt, legacy, "com.acme.shop.web.LegacyFilter.doFilter(ServletRequest,ServletResponse,FilterChain)", "public void doFilter");
        var li = r.File(J + "web/LegacyInterceptor.java", """
            public class LegacyInterceptor extends HandlerInterceptorAdapter {
                public boolean preHandle(HttpServletRequest request, HttpServletResponse response, Object handler) { return true; }
            }
            """);
        var lit = r.Type(li, "com.acme.shop.web.LegacyInterceptor", "public class LegacyInterceptor");
        r.Method(lit, li, "com.acme.shop.web.LegacyInterceptor.preHandle(HttpServletRequest,HttpServletResponse,Object)", "public boolean preHandle");
        var tie = r.File(J + "aop/AuditAspect.java", """
            @Aspect
            @Order(2)
            public class AuditAspect {
                @Before("serviceLayer()")
                public void note() { }
            }
            """);
        var tt = r.Type(tie, "com.acme.shop.aop.AuditAspect", "public class AuditAspect");
        r.Method(tt, tie, "com.acme.shop.aop.AuditAspect.note()", "public void note");

        var f = FrameworkFlowBuilder.Build(r.Input("shop-api", "shop-api"));
        var stage = f.Stages.ToDictionary(s => s.Key, s => s.OrderKnown);
        Assert.False(stage["middleware"]);
        Assert.False(stage["interceptor"]);
        Assert.False(stage["aspect"]);
        Assert.False(stage["tail"]);
        Assert.True(stage["controller"]);
        var root = f.Flow.Root;
        var lastFilter = root.Children.Last(c => c.Actor == "middleware");
        Assert.Equal("LegacyFilter.doFilter", lastFilter.Label);
        Assert.Contains("没找到注册位置", lastFilter.Note);
        var dispatcher = root.Children.Single(c => c.Actor == "dispatcher");
        var lastInterceptor = dispatcher.Children.Last(c => c.Actor == "interceptor");
        Assert.Equal("LegacyInterceptor.preHandle", lastInterceptor.Label);
        Assert.Contains("没找到 registry.addInterceptor", lastInterceptor.Note);
        Assert.Contains(f.Notes, n => n.Contains("AuditAspect 与 LoggingAspect 没有区分的 @Order"));
        Assert.Contains(f.Notes, n => n.Contains("1 个过滤器没找到注册位置"));
        // 命名切点在别的切面里也展开
        Assert.Contains(All(root), n => n.Label == "AuditAspect.note" && n.Note!.Contains("serviceLayer() = execution("));
    }

    // ================= 夹具二：WebFlux + Spring Cloud Gateway =================

    private const string G = "shop-gateway/src/main/java/com/acme/gateway/";

    private static Repo GatewayFixture()
    {
        var r = new Repo();
        r.File("shop-gateway/pom.xml", "<project><dependencies><dependency><artifactId>spring-cloud-starter-gateway</artifactId></dependency></dependencies></project>");
        r.File("shop-gateway/src/main/resources/application.yml", """
            spring:
              application:
                name: shop-gateway
              cloud:
                gateway:
                  default-filters:
                    - name: CircuitBreaker
                      args:
                        name: defaultCircuitBreaker
                    - TenantHeader
                  routes:
                    - id: orders
                      uri: lb://shop-orders
                      predicates:
                        - Path=/api/orders/**
                      filters:
                        - StripPrefix=1
                    - id: catalog
                      uri: lb://shop-catalog
                      predicates:
                        - Path=/api/catalog/**
            """);

        var trace = r.File(G + "TraceWebFilter.java", """
            @Component
            @Order(-1)
            public class TraceWebFilter implements WebFilter {
                public Mono<Void> filter(ServerWebExchange exchange, WebFilterChain chain) {
                    Tracing.start(exchange);
                    return chain.filter(exchange).doFinally(s -> Tracing.end(exchange));
                }
            }
            """);
        var tt = r.Type(trace, "com.acme.gateway.TraceWebFilter", "public class TraceWebFilter");
        var tm = r.Method(tt, trace, "com.acme.gateway.TraceWebFilter.filter(ServerWebExchange,WebFilterChain)", "public Mono<Void> filter", "public Mono<Void> filter(ServerWebExchange exchange, WebFilterChain chain)");
        r.Body(tm, r.Call(trace, "Tracing.start", 950, "Tracing.start", "com.acme.gateway.Tracing.start(ServerWebExchange)"),
            r.Ext(trace, "chain.filter", "WebFilterChain.filter"), r.Ext(trace, "doFinally", "Mono.doFinally"),
            r.Call(trace, "Tracing.end", 951, "Tracing.end", "com.acme.gateway.Tracing.end(ServerWebExchange)", ctx: CallContext.Callback));

        var authG = r.File(G + "AuthGlobalFilter.java", """
            @Component
            public class AuthGlobalFilter implements GlobalFilter, Ordered {
                public Mono<Void> filter(ServerWebExchange exchange, GatewayFilterChain chain) {
                    TokenCheck.verify(exchange);
                    return chain.filter(exchange);
                }

                public int getOrder() {
                    return Ordered.HIGHEST_PRECEDENCE + 10;
                }
            }
            """);
        var at = r.Type(authG, "com.acme.gateway.AuthGlobalFilter", "public class AuthGlobalFilter");
        var am = r.Method(at, authG, "com.acme.gateway.AuthGlobalFilter.filter(ServerWebExchange,GatewayFilterChain)", "public Mono<Void> filter", "public Mono<Void> filter(ServerWebExchange exchange, GatewayFilterChain chain)");
        r.Method(at, authG, "com.acme.gateway.AuthGlobalFilter.getOrder()", "public int getOrder");
        r.Body(am, r.Call(authG, "TokenCheck.verify", 952, "TokenCheck.verify", "com.acme.gateway.TokenCheck.verify(ServerWebExchange)"), r.Ext(authG, "chain.filter", "GatewayFilterChain.filter"));

        var late = r.File(G + "AccessLogGlobalFilter.java", """
            @Component
            public class AccessLogGlobalFilter implements GlobalFilter {
                public Mono<Void> filter(ServerWebExchange exchange, GatewayFilterChain chain) {
                    return chain.filter(exchange).then(Mono.fromRunnable(() -> AccessLog.write(exchange)));
                }
            }
            """);
        var lt = r.Type(late, "com.acme.gateway.AccessLogGlobalFilter", "public class AccessLogGlobalFilter");
        var lm = r.Method(lt, late, "com.acme.gateway.AccessLogGlobalFilter.filter(ServerWebExchange,GatewayFilterChain)", "public Mono<Void> filter", "public Mono<Void> filter(ServerWebExchange exchange, GatewayFilterChain chain)");
        r.Body(lm, r.Ext(late, "chain.filter", "GatewayFilterChain.filter"), r.Ext(late, "then(", "Mono.then"),
            r.Call(late, "AccessLog.write", 953, "AccessLog.write", "com.acme.gateway.AccessLog.write(ServerWebExchange)", ctx: CallContext.Callback));

        // 自研过滤器工厂：apply 返回 lambda，(exchange, chain) 没有声明类型
        var factory = r.File(G + "TenantHeaderGatewayFilterFactory.java", """
            @Component
            public class TenantHeaderGatewayFilterFactory extends AbstractGatewayFilterFactory<Object> {
                public GatewayFilter apply(Object config) {
                    return (exchange, chain) -> {
                        TenantHeader.add(exchange);
                        return chain.filter(exchange);
                    };
                }
            }
            """);
        var ft = r.Type(factory, "com.acme.gateway.TenantHeaderGatewayFilterFactory", "public class TenantHeaderGatewayFilterFactory");
        var fm = r.Method(ft, factory, "com.acme.gateway.TenantHeaderGatewayFilterFactory.apply(Object)", "public GatewayFilter apply", "public GatewayFilter apply(Object config)");
        r.Body(fm, r.Call(factory, "TenantHeader.add", 954, "TenantHeader.add", "com.acme.gateway.TenantHeader.add(ServerWebExchange)", ctx: CallContext.Callback),
            r.Ext(factory, "chain.filter", "filter", CallContext.Callback));

        var fallback = r.File(G + "FallbackController.java", """
            @RestController
            public class FallbackController {
                @GetMapping("/fallback")
                public Mono<String> fallback() { return Mono.just("down"); }
            }
            """);
        var fbt = r.Type(fallback, "com.acme.gateway.FallbackController", "public class FallbackController");
        var fbm = r.Method(fbt, fallback, "com.acme.gateway.FallbackController.fallback()", "public Mono<String> fallback");
        r.Entries.Add(new EntryPoint(fbm, "shop-gateway", "spring.mvc", "http", "/fallback", "GET", fallback, 4));
        return r;
    }

    [Fact]
    public void Gateway_WebFilterThenDispatcherHandler_GlobalAndDefaultFiltersMerged()
    {
        var f = FrameworkFlowBuilder.Build(GatewayFixture().Input("shop-gateway", "shop-gateway"));

        Assert.Equal(["middleware", "dispatcher", "controller", "tail"], f.Stages.Select(s => s.Key));
        var root = f.Flow.Root;
        Assert.Equal(["TraceWebFilter.filter", "DispatcherHandler（Spring WebFlux 内置）", "Tracing.end"], Labels(root));
        Assert.Equal(["Tracing.start"], Labels(root.Children[0]));                      // Mono.doFinally 是噪音
        Assert.Equal("TraceWebFilter 后置 · 回调 / lambda 里", root.Children[2].Note);
        var dispatcher = root.Children[1];
        Assert.Equal(["网关过滤器链（Spring Cloud Gateway）", "控制器"], Labels(dispatcher));
        var chain = dispatcher.Children[0];
        Assert.Equal("middleware", chain.Actor);
        // AuthGlobalFilter（HIGHEST + 10）→ CircuitBreaker（1）→ TenantHeader 工厂（2）→ AccessLogGlobalFilter（没写顺序，LOWEST）→ 路由过滤器 → 转发 → 后置
        Assert.Equal(["AuthGlobalFilter.filter", "CircuitBreaker", "TenantHeaderGatewayFilterFactory.apply", "AccessLogGlobalFilter.filter", "各路由自己的过滤器", "转发到下游服务", "AccessLog.write"],
            Labels(chain));
        Assert.Equal(["TokenCheck.verify"], Labels(chain.Children[0]));
        Assert.Contains("getOrder() = HIGHEST_PRECEDENCE + 10", chain.Children[0].Note);
        Assert.Contains("default-filters 第 1 个（application.yml:7）", chain.Children[1].Note);
        Assert.Equal(["TenantHeader.add"], Labels(chain.Children[2]));                 // lambda 里没类型的 chain.filter 也认
        Assert.Contains("自研过滤器工厂", chain.Children[2].Note);
        Assert.Contains("LOWEST_PRECEDENCE", chain.Children[3].Note);
        Assert.Equal("orders: StripPrefix", chain.Children[4].Note);
        Assert.Equal(("tail", "AccessLogGlobalFilter 后置 · 回调 / lambda 里"), (chain.Children[6].Actor, chain.Children[6].Note));
        Assert.Equal(1, dispatcher.Children[1].Count);

        Assert.Contains(f.Notes, n => n.StartsWith("Spring WebFlux + Spring Cloud Gateway", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("orders（/api/orders/**） → lb://shop-orders") && n.Contains("catalog"));
        Assert.Contains(f.Notes, n => n.Contains("2 个 GlobalFilter，2 个 default-filters，2 条路由"));
        Assert.DoesNotContain(f.Stages, s => s.Key == "interceptor");
    }

    // ================= 夹具三：非 Web；夹具四：只有控制器 + 框架切面 =================

    [Fact]
    public void NonWebContainer_EntryPlaceholderWithTransactional()
    {
        var r = new Repo();
        r.File("shop-worker/pom.xml", "<project><dependencies><dependency><artifactId>spring-boot-starter</artifactId></dependency></dependencies></project>");
        var job = r.File("shop-worker/src/main/java/com/acme/worker/ReportJob.java", """
            @Component
            public class ReportJob {
                @Scheduled(cron = "0 0 * * * *")
                @Transactional
                public void run() { }
            }
            """);
        var t = r.Type(job, "com.acme.worker.ReportJob", "public class ReportJob");
        var m = r.Method(t, job, "com.acme.worker.ReportJob.run()", "public void run");
        r.Entries.Add(new EntryPoint(m, "shop-worker", "spring.scheduled", "job", "0 0 * * * *", null, job, 5));
        r.Entries.Add(new EntryPoint(t, "shop-worker", "spring.boot.main", "hosted", null, null, job, 2));
        var f = FrameworkFlowBuilder.Build(r.Input("shop-worker", "shop-worker"));

        Assert.Equal(["controller", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal("入口", f.Stages[0].Label);
        var root = f.Flow.Root;
        Assert.Equal(("入口", "controller"), (root.Label, root.Actor));
        var entry = Assert.Single(root.Children);
        Assert.Equal(("入口（常驻服务 / 任务 / 消息消费）", "controllers", 2), (entry.Label, entry.Kind, entry.Count));
        Assert.Equal(["@Transactional 提交 / 回滚"], Labels(entry));
        Assert.Contains(f.Notes, n => n.StartsWith("这个容器不是 Web 服务", StringComparison.Ordinal));
    }

    [Fact]
    public void PlainMvc_FrameworkAspectsAndCache_NotesExplainMissingStages()
    {
        var r = new Repo();
        r.File("pom.xml", "<project><dependencies><dependency><artifactId>spring-boot-starter-webmvc</artifactId></dependency></dependencies></project>");
        var cfg = r.File("src/main/java/com/acme/MetricConfig.java", """
            @Configuration
            public class MetricConfig {
                @Bean
                TimedAspect timedAspect(MeterRegistry registry) {
                    return new TimedAspect(registry);
                }
            }
            """);
        var ct = r.Type(cfg, "com.acme.MetricConfig", "public class MetricConfig");
        r.Method(ct, cfg, "com.acme.MetricConfig.timedAspect(MeterRegistry)", "TimedAspect timedAspect", "TimedAspect timedAspect(MeterRegistry registry)");
        var res = r.File("src/main/java/com/acme/OwnerResource.java", """
            @RestController
            @Timed("shop.owner")
            public class OwnerResource {
                @GetMapping("/owners")
                public List<Owner> list() { return null; }
            }
            """);
        var rt = r.Type(res, "com.acme.OwnerResource", "public class OwnerResource");
        var list = r.Method(rt, res, "com.acme.OwnerResource.list()", "public List<Owner> list");
        var repo = r.File("src/main/java/com/acme/VetRepository.java", """
            public interface VetRepository extends Repository<Vet, Integer> {
                @Cacheable("vets")
                Collection<Vet> findAll();
            }
            """);
        var vt = r.Type(repo, "com.acme.VetRepository", "public interface VetRepository", SymbolKind.Interface);
        r.Symbols.Add(new MapSymbol(999, SymbolKind.Method, "com.acme.VetRepository.findAll()", repo, vt, 3, 3));
        r.Entries.Add(new EntryPoint(list, "", "spring.mvc", "http", "/owners", "GET", res, 5));
        var f = FrameworkFlowBuilder.Build(r.Input("", ""));

        Assert.Equal(["dispatcher", "controller", "aspect"], f.Stages.Select(s => s.Key));
        var root = f.Flow.Root;
        Assert.Equal(("HTTP 请求", "dispatcher"), (root.Label, root.Actor));
        var controllers = Assert.Single(Assert.Single(root.Children).Children);
        Assert.Equal(["TimedAspect", "缓存代理（Spring 内置）"], Labels(controllers));
        Assert.Contains("对 @Timed 标注的类 / 方法计时（1 处，如 OwnerResource）", controllers.Children[0].Note);
        Assert.Contains("@Cacheable 1 处，如 VetRepository.findAll", controllers.Children[1].Note);
        Assert.Contains(f.Notes, n => n.StartsWith("没有发现自研过滤器", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.StartsWith("没有发现拦截器", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.StartsWith("没有发现 @Aspect 切面（有框架切面 / 代理：TimedAspect、缓存代理）", StringComparison.Ordinal));
        Assert.Contains("没有发现 @Transactional", f.Notes);
        Assert.Contains(f.Notes, n => n.StartsWith("没有发现异常处理", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.StartsWith("没有发现收尾", StringComparison.Ordinal));
    }

    [Fact]
    public void SecurityAdapterStyle_ChainedDsl_GraphQLExceptionHandler()
    {
        // 老写法：extends WebSecurityConfigurerAdapter + configure(HttpSecurity)，链式 .csrf().disable().cors().and()…；Gradle 依赖；DGS 的 DataFetcherExceptionHandler
        var r = new Repo();
        r.File("build.gradle", """
            dependencies {
                implementation 'org.springframework.boot:spring-boot-starter-web'
                implementation 'org.springframework.boot:spring-boot-starter-security'
            }
            """);
        var cfg = r.File("src/main/java/com/acme/security/WebSecurityConfig.java", """
            @Configuration
            @EnableWebSecurity
            public class WebSecurityConfig extends WebSecurityConfigurerAdapter {
                @Bean
                public TokenFilter tokenFilter() {
                    return new TokenFilter();
                }

                @Override
                protected void configure(HttpSecurity http) throws Exception {
                    http.csrf()
                        .disable()
                        .cors()
                        .and()
                        .sessionManagement()
                        .sessionCreationPolicy(SessionCreationPolicy.STATELESS);
                    http.addFilterBefore(tokenFilter(), UsernamePasswordAuthenticationFilter.class);
                }
            }
            """);
        var ct = r.Type(cfg, "com.acme.security.WebSecurityConfig", "public class WebSecurityConfig");
        r.Method(ct, cfg, "com.acme.security.WebSecurityConfig.tokenFilter()", "public TokenFilter tokenFilter", "public TokenFilter tokenFilter()");
        r.Method(ct, cfg, "com.acme.security.WebSecurityConfig.configure(HttpSecurity)", "protected void configure", "protected void configure(HttpSecurity http) throws Exception");
        var tf = r.File("src/main/java/com/acme/security/TokenFilter.java", """
            public class TokenFilter extends OncePerRequestFilter {
                protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain filterChain) {
                    filterChain.doFilter(request, response);
                }
            }
            """);
        var tt = r.Type(tf, "com.acme.security.TokenFilter", "public class TokenFilter");
        r.Method(tt, tf, "com.acme.security.TokenFilter.doFilterInternal(HttpServletRequest,HttpServletResponse,FilterChain)", "protected void doFilterInternal",
            "protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain filterChain)");
        var gq = r.File("src/main/java/com/acme/graphql/GraphQLExceptionHandler.java", """
            @Component
            public class GraphQLExceptionHandler implements DataFetcherExceptionHandler {
                @Override
                public DataFetcherExceptionHandlerResult onException(DataFetcherExceptionHandlerParameters p) {
                    return Errors.toResult(p);
                }
            }
            """);
        var gt = r.Type(gq, "com.acme.graphql.GraphQLExceptionHandler", "public class GraphQLExceptionHandler");
        var gm = r.Method(gt, gq, "com.acme.graphql.GraphQLExceptionHandler.onException(DataFetcherExceptionHandlerParameters)", "public DataFetcherExceptionHandlerResult onException");
        r.Body(gm, r.Call(gq, "Errors.toResult", 960, "Errors.toResult", "com.acme.graphql.Errors.toResult(DataFetcherExceptionHandlerParameters)"));
        var f = FrameworkFlowBuilder.Build(r.Input("", ""));

        var root = f.Flow.Root;
        Assert.Equal(["Spring Security 过滤器链", "DispatcherServlet（Spring 内置）"], Labels(root));
        var security = root.Children[0];
        Assert.Contains("WebSecurityConfigurerAdapter（WebSecurityConfig.java:3）", security.Note);
        Assert.Contains("配置了 csrf（关闭）、cors、sessionManagement", security.Note);
        var token = Assert.Single(security.Children);
        Assert.Equal("TokenFilter.doFilterInternal", token.Label);
        Assert.Contains("同时是 @Bean tokenFilter()", token.Note);
        Assert.DoesNotContain("没找到", token.Note);
        var handler = Assert.Single(root.Children[1].Children);
        Assert.Equal(("GraphQLExceptionHandler.onException", "tail"), (handler.Label, handler.Actor));
        Assert.Contains("GraphQL", handler.Note);
        Assert.Equal(["Errors.toResult"], Labels(handler));
    }

    // ================= 小工具 =================

    [Fact]
    public void GatewayConfig_ReadsProperties()
    {
        var files = new Dictionary<string, string>
        {
            ["gw/src/main/resources/application.properties"] = """
                spring.cloud.gateway.default-filters[1]=AddResponseHeader=X-Gw, 1
                spring.cloud.gateway.default-filters[0]=DedupeResponseHeader=Access-Control-Allow-Origin
                spring.cloud.gateway.routes[0].id=orders
                spring.cloud.gateway.routes[0].uri=lb://orders
                spring.cloud.gateway.routes[0].predicates[0]=Path=/api/orders/**
                spring.cloud.gateway.routes[0].filters[0]=StripPrefix=1
                """,
        };
        var (defaults, routes) = SpringFrameworkFlow.GatewayConfig(p => files.GetValueOrDefault(p), "gw");
        Assert.Equal(["DedupeResponseHeader", "AddResponseHeader"], defaults.Select(d => d.Name));
        Assert.Equal("X-Gw, 1", defaults[1].Args);
        var route = Assert.Single(routes);
        Assert.Equal(("orders", "/api/orders/**", "lb://orders", "StripPrefix"), (route.Id, route.Path, route.Uri, route.Filters.Single().Name));
    }

    [Fact]
    public void Dependencies_SkipTestScopes_GradleAndMaven()
    {
        var files = new Dictionary<string, string>
        {
            ["svc/build.gradle"] = """
                dependencies {
                    implementation 'org.springframework.boot:spring-boot-starter-web'
                    implementation("org.springframework.boot:spring-boot-starter-security:3.2.0")
                    testImplementation 'org.springframework.boot:spring-boot-starter-webflux'
                }
                """,
        };
        Assert.Equal(["spring-boot-starter-web", "spring-boot-starter-security"], SpringFrameworkFlow.Dependencies(p => files.GetValueOrDefault(p), "svc"));
    }

    [Theory]
    [InlineData(SpringFrameworkFlow.Highest, "HIGHEST_PRECEDENCE")]
    [InlineData(SpringFrameworkFlow.Highest + 10, "HIGHEST_PRECEDENCE + 10")]
    [InlineData(SpringFrameworkFlow.Lowest, "LOWEST_PRECEDENCE")]
    [InlineData(-100, "-100")]
    public void OrderText_NamesPrecedenceConstants(long value, string expected) => Assert.Equal(expected, SpringFrameworkFlow.OrderText(value));

    [Fact]
    public void JavaKeep_DropsNoiseAndBareNames()
    {
        static FrameworkCall Ext(string qn) => new(1, "external", qn, qn, 1, 1, null, false, []);
        Assert.True(SpringFrameworkFlow.JavaKeep(Ext("external:HttpServletRequest.getHeader")));
        Assert.False(SpringFrameworkFlow.JavaKeep(Ext("external:Optional.ofNullable")));
        Assert.False(SpringFrameworkFlow.JavaKeep(Ext("external:orElseThrow")));
        Assert.False(SpringFrameworkFlow.JavaKeep(Ext("external:SessionLocaleResolver")));
        Assert.False(SpringFrameworkFlow.JavaKeep(Ext("external:MethodArgumentNotValidException.stream"))); // 链式调用被记成链头的类型
    }
}
