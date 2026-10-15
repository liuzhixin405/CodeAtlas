using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>Python 入口：FastAPI（APIRouter prefix + include_router 前缀链 + 常量）、Flask（Blueprint url_prefix 被 register_blueprint 覆盖）、
/// Django（urls.py 的 include 前缀链、类视图、DRF router.register）、gRPC servicer、Celery 任务、__main__。
/// 符号的 StartLine 是 def 行（不含装饰器）。</summary>
public partial class EntryDiscoveryTests
{
    private static readonly Dictionary<string, string[]> PyFiles = new()
    {
        ["backend/app/main.py"] =
        [
            "from fastapi import FastAPI",                                 // 1
            "from app.api.main import api_router",                         // 2
            "from app.core.config import settings",                        // 3
            "",                                                            // 4
            "app = FastAPI(title=settings.PROJECT_NAME)",                  // 5
            "app.include_router(api_router, prefix=settings.API_V1_STR)",  // 6：常量在 core/config.py
            "",                                                            // 7
            "@app.get(\"/healthz\")",                                      // 8
            "def healthz():",                                              // 9
            "    return {}",                                               // 10
        ],
        ["backend/app/api/main.py"] =
        [
            "from fastapi import APIRouter",                  // 1
            "from app.api.routes import items, login",        // 2
            "",                                               // 3
            "api_router = APIRouter()",                       // 4
            "api_router.include_router(login.router)",        // 5
            "api_router.include_router(items.router)",        // 6
        ],
        ["backend/app/api/routes/items.py"] =
        [
            "from fastapi import APIRouter",                          // 1
            "",                                                       // 2
            "router = APIRouter(prefix=\"/items\", tags=[\"items\"])", // 3
            "",                                                       // 4
            "",                                                       // 5
            "@router.get(\"/\", response_model=ItemsPublic)",         // 6
            "def read_items(session: SessionDep):",                   // 7
            "    pass",                                               // 8
            "",                                                       // 9
            "@router.put(",                                           // 10：多行装饰器
            "    \"/{id}\",",                                         // 11
            "    response_model=ItemPublic,",                         // 12
            ")",                                                      // 13
            "async def update_item(id: int):",                        // 14
            "    pass",                                               // 15
        ],
        ["backend/app/api/routes/login.py"] =
        [
            "from fastapi import APIRouter",               // 1
            "",                                            // 2
            "router = APIRouter(tags=[\"login\"])",        // 3：没有 prefix
            "",                                            // 4
            "@router.post(\"/login/access-token\")",       // 5
            "def login_access_token():",                   // 6
            "    pass",                                    // 7
        ],
        ["backend/app/core/config.py"] =
        [
            "class Settings(BaseSettings):",              // 1
            "    API_V1_STR: str = \"/api/v1\"",          // 2
            "    PROJECT_NAME: str = \"x\"",              // 3
        ],
        ["flaskapp/app.py"] =
        [
            "from flask import Flask",                            // 1
            "from .views import bp",                              // 2
            "app = Flask(__name__)",                              // 3
            "app.register_blueprint(bp, url_prefix=\"/shop\")",   // 4：覆盖 Blueprint 自己的 url_prefix
            "",                                                   // 5
            "@app.route(\"/\", methods=[\"GET\", \"POST\"])",      // 6
            "def index():",                                       // 7
            "    pass",                                           // 8
            "",                                                   // 9
            "@celery.task",                                       // 10
            "def send_mail():",                                   // 11
            "    pass",                                           // 12
            "",                                                   // 13
            "def main():",                                        // 14
            "    app.run()",                                      // 15
            "",                                                   // 16
            "if __name__ == \"__main__\":",                       // 17
            "    main()",                                         // 18
        ],
        ["flaskapp/views.py"] =
        [
            "from flask import Blueprint",                              // 1
            "bp = Blueprint(\"shop\", __name__, url_prefix=\"/api\")",  // 2
            "",                                                         // 3
            "@bp.get(\"/items/<int:item_id>\")",                        // 4
            "def get_item(item_id):",                                   // 5
            "    pass",                                                 // 6
            "",                                                         // 7
            "@shared_task(bind=True)",                                  // 8
            "def rebuild(self):",                                       // 9
            "    pass",                                                 // 10
        ],
        ["conduit/urls.py"] =
        [
            "from django.conf.urls import include, url",                                      // 1
            "from . import views",                                                            // 2
            "urlpatterns = [",                                                                // 3
            "    url(r'^admin/', admin.site.urls),",                                          // 4：不是视图 → 不算
            "    url(r'^api/', include('conduit.apps.articles.urls', namespace='articles')),", // 5
            "    path(\"health/\", views.health),",                                           // 6
            "]",                                                                              // 7
        ],
        ["conduit/views.py"] =
        [
            "def health(request):",  // 1
            "    pass",              // 2
        ],
        ["conduit/apps/articles/urls.py"] =
        [
            "from rest_framework.routers import DefaultRouter",                        // 1
            "from .views import ArticleViewSet, ArticlesFeedAPIView",                  // 2
            "",                                                                        // 3
            "router = DefaultRouter(trailing_slash=False)",                            // 4
            "router.register(r'articles', ArticleViewSet)",                            // 5
            "",                                                                        // 6
            "urlpatterns = [",                                                         // 7
            "    url(r'^', include(router.urls)),",                                    // 8
            "    url(r'^articles/feed/?$', ArticlesFeedAPIView.as_view()),",           // 9
            "    re_path(r'^articles/(?P<article_slug>[-\\w]+)/comments/?$',",         // 10：跨行
            "        CommentsAPIView.as_view()),",                                     // 11
            "]",                                                                       // 12
        ],
        ["conduit/apps/articles/views.py"] =
        [
            "class ArticleViewSet(mixins.CreateModelMixin, viewsets.GenericViewSet):", // 1
            "    lookup_field = 'slug'",                                              // 2
            "",                                                                       // 3
            "    def create(self, request):",                                         // 4
            "        pass",                                                           // 5
            "",                                                                       // 6
            "    def list(self, request):",                                           // 7
            "        pass",                                                           // 8
            "",                                                                       // 9
            "    def retrieve(self, request, slug):",                                 // 10
            "        pass",                                                           // 11
            "",                                                                       // 12
            "    def _helper(self):",                                                 // 13
            "        pass",                                                           // 14
            "",                                                                       // 15
            "class ArticlesFeedAPIView(generics.ListAPIView):",                       // 16
            "    def list(self, request):",                                           // 17
            "        pass",                                                           // 18
            "",                                                                       // 19
            "class CommentsAPIView(APIView):",                                        // 20
            "    def get(self, request, article_slug=None):",                         // 21
            "        pass",                                                           // 22
            "    def post(self, request, article_slug=None):",                        // 23
            "        pass",                                                           // 24
        ],
        ["recommendationservice/recommendation_server.py"] =
        [
            "class RecommendationService(demo_pb2_grpc.RecommendationServiceServicer):", // 1
            "    def ListRecommendations(self, request, context):",                    // 2
            "        cat_response = product_catalog_stub.ListProducts(demo_pb2.Empty())", // 3
            "        return x",                                                        // 4
            "    def Check(self, request, context):",                                  // 5
            "        pass",                                                            // 6
            "    def _cache(self):",                                                   // 7
            "        pass",                                                            // 8
            "",                                                                        // 9
            "if __name__ == \"__main__\":",                                            // 10
            "    initStackdriverProfiling(); channel = grpc.insecure_channel(catalog_addr)", // 11：块里调的本模块函数不是启动函数 → 不取它
            "    product_catalog_stub = demo_pb2_grpc.ProductCatalogServiceStub(channel)", // 12
            "    server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))",    // 13
            "    service = RecommendationService()",                                   // 14
            "    demo_pb2_grpc.add_RecommendationServiceServicer_to_server(service, server)", // 15
            "    health_pb2_grpc.add_HealthServicer_to_server(service, server)",       // 16：类没继承 HealthServicer → 不算
            "def initStackdriverProfiling():",                                         // 17
            "    pass",                                                                // 18
        ],
        ["backend/tests/test_items.py"] =
        [
            "@router.get(\"/x\")",   // 1：测试文件
            "def test_x():",         // 2
            "    pass",              // 3
        ],
    };

    private static readonly List<MapSymbol> PySymbols =
    [
        Sym(2000, SymbolKind.Module, "backend.app.main", "backend/app/main.py", null, 1, 10),
        Sym(2001, SymbolKind.Function, "backend.app.main.healthz", "backend/app/main.py", 2000, 9, 10),
        Sym(2002, SymbolKind.Module, "backend.app.api.main", "backend/app/api/main.py", null, 1, 6),
        Sym(2003, SymbolKind.Module, "backend.app.api.routes.items", "backend/app/api/routes/items.py", null, 1, 15),
        Sym(2004, SymbolKind.Function, "backend.app.api.routes.items.read_items", "backend/app/api/routes/items.py", 2003, 7, 8),
        Sym(2005, SymbolKind.Function, "backend.app.api.routes.items.update_item", "backend/app/api/routes/items.py", 2003, 14, 15),
        Sym(2006, SymbolKind.Module, "backend.app.api.routes.login", "backend/app/api/routes/login.py", null, 1, 7),
        Sym(2007, SymbolKind.Function, "backend.app.api.routes.login.login_access_token", "backend/app/api/routes/login.py", 2006, 6, 7),
        Sym(2008, SymbolKind.Module, "backend.app.core.config", "backend/app/core/config.py", null, 1, 3),
        Sym(2009, SymbolKind.Class, "backend.app.core.config.Settings", "backend/app/core/config.py", 2008, 1, 3),
        Sym(2010, SymbolKind.Module, "backend.tests.test_items", "backend/tests/test_items.py", null, 1, 3),
        Sym(2011, SymbolKind.Function, "backend.tests.test_items.test_x", "backend/tests/test_items.py", 2010, 2, 3),
        Sym(2020, SymbolKind.Module, "flaskapp.app", "flaskapp/app.py", null, 1, 18),
        Sym(2021, SymbolKind.Function, "flaskapp.app.index", "flaskapp/app.py", 2020, 7, 8),
        Sym(2022, SymbolKind.Function, "flaskapp.app.send_mail", "flaskapp/app.py", 2020, 11, 12),
        Sym(2023, SymbolKind.Function, "flaskapp.app.main", "flaskapp/app.py", 2020, 14, 15),
        Sym(2024, SymbolKind.Module, "flaskapp.views", "flaskapp/views.py", null, 1, 10),
        Sym(2025, SymbolKind.Function, "flaskapp.views.get_item", "flaskapp/views.py", 2024, 5, 6),
        Sym(2026, SymbolKind.Function, "flaskapp.views.rebuild", "flaskapp/views.py", 2024, 9, 10),
        Sym(2040, SymbolKind.Module, "conduit.urls", "conduit/urls.py", null, 1, 7),
        Sym(2041, SymbolKind.Module, "conduit.views", "conduit/views.py", null, 1, 2),
        Sym(2042, SymbolKind.Function, "conduit.views.health", "conduit/views.py", 2041, 1, 2),
        Sym(2043, SymbolKind.Module, "conduit.apps.articles.urls", "conduit/apps/articles/urls.py", null, 1, 12),
        Sym(2044, SymbolKind.Module, "conduit.apps.articles.views", "conduit/apps/articles/views.py", null, 1, 24),
        Sym(2045, SymbolKind.Class, "conduit.apps.articles.views.ArticleViewSet", "conduit/apps/articles/views.py", 2044, 1, 14),
        Sym(2046, SymbolKind.Method, "conduit.apps.articles.views.ArticleViewSet.create", "conduit/apps/articles/views.py", 2045, 4, 5),
        Sym(2047, SymbolKind.Method, "conduit.apps.articles.views.ArticleViewSet.list", "conduit/apps/articles/views.py", 2045, 7, 8),
        Sym(2048, SymbolKind.Method, "conduit.apps.articles.views.ArticleViewSet.retrieve", "conduit/apps/articles/views.py", 2045, 10, 11),
        Sym(2049, SymbolKind.Method, "conduit.apps.articles.views.ArticleViewSet._helper", "conduit/apps/articles/views.py", 2045, 13, 14),
        Sym(2050, SymbolKind.Class, "conduit.apps.articles.views.ArticlesFeedAPIView", "conduit/apps/articles/views.py", 2044, 16, 18),
        Sym(2051, SymbolKind.Method, "conduit.apps.articles.views.ArticlesFeedAPIView.list", "conduit/apps/articles/views.py", 2050, 17, 18),
        Sym(2052, SymbolKind.Class, "conduit.apps.articles.views.CommentsAPIView", "conduit/apps/articles/views.py", 2044, 20, 24),
        Sym(2053, SymbolKind.Method, "conduit.apps.articles.views.CommentsAPIView.get", "conduit/apps/articles/views.py", 2052, 21, 22),
        Sym(2054, SymbolKind.Method, "conduit.apps.articles.views.CommentsAPIView.post", "conduit/apps/articles/views.py", 2052, 23, 24),
        Sym(2060, SymbolKind.Module, "recommendationservice.recommendation_server", "recommendationservice/recommendation_server.py", null, 1, 18),
        Sym(2061, SymbolKind.Class, "recommendationservice.recommendation_server.RecommendationService", "recommendationservice/recommendation_server.py", 2060, 1, 8),
        Sym(2062, SymbolKind.Method, "recommendationservice.recommendation_server.RecommendationService.ListRecommendations", "recommendationservice/recommendation_server.py", 2061, 2, 4),
        Sym(2063, SymbolKind.Method, "recommendationservice.recommendation_server.RecommendationService.Check", "recommendationservice/recommendation_server.py", 2061, 5, 6),
        Sym(2064, SymbolKind.Method, "recommendationservice.recommendation_server.RecommendationService._cache", "recommendationservice/recommendation_server.py", 2061, 7, 8),
        Sym(2065, SymbolKind.Function, "recommendationservice.recommendation_server.initStackdriverProfiling", "recommendationservice/recommendation_server.py", 2060, 17, 18),
    ];

    private static readonly List<ArchNode> PyContainers =
    [
        Container("backend", "python", "backend"),
        Container("flaskapp", "python", "flaskapp"),
        Container("conduit", "python", "conduit"),
        Container("recommendationservice", "python", "recommendationservice"),
    ];

    private static List<EntryPoint> PyEntries() =>
        EntryDiscovery.Discover(PySymbols, [], PyContainers, p => PyFiles.TryGetValue(p, out var l) ? l : []);

    [Fact]
    public void Python_FastApi_RouterPrefixAndIncludeRouterChain_ConstantResolved_MultiLineDecorator()
    {
        var fastapi = PyEntries().Where(e => e.RuleId == "python.fastapi").ToList();
        Assert.All(fastapi, e => Assert.Equal("http", e.Kind));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("backend", "/api/v1/items/", "GET", 2004, "backend/app/api/routes/items.py", 7),          // /api/v1 + /items + /：FastAPI 区分尾斜杠，原样保留
            ("backend", "/api/v1/items/{id}", "PUT", 2005, "backend/app/api/routes/items.py", 14),      // 多行装饰器
            ("backend", "/api/v1/login/access-token", "POST", 2007, "backend/app/api/routes/login.py", 6), // 路由器没有 prefix
            ("backend", "/healthz", "GET", 2001, "backend/app/main.py", 9),                              // app 自己的路由
        }, Rows(fastapi));
        Assert.DoesNotContain(PyEntries(), e => e.SymbolId == 2011); // tests/ 下的不算
    }

    [Fact]
    public void Python_NestedContainers_EntriesGoToTheMostSpecificContainerOnly()
    {
        // 仓库根还有一个成员为 "" 的容器（uv workspace 根的 pyproject）：backend 下的路由只归 backend
        var entries = EntryDiscovery.Discover(PySymbols, [], [.. PyContainers, Container("repo-root", "python", "")], p => PyFiles.TryGetValue(p, out var l) ? l : []);
        Assert.DoesNotContain(entries, e => e.ContainerKey == "repo-root");
        Assert.Equal(4, entries.Count(e => e.RuleId == "python.fastapi" && e.ContainerKey == "backend"));
    }

    [Fact]
    public void Python_Flask_RouteMethods_BlueprintPrefixOverriddenByRegister_ConverterParams()
    {
        var flask = PyEntries().Where(e => e.RuleId == "python.flask").ToList();
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("flaskapp", "/", "GET", 2021, "flaskapp/app.py", 7),                       // methods=[...] 每个方法一个入口
            ("flaskapp", "/", "POST", 2021, "flaskapp/app.py", 7),
            ("flaskapp", "/shop/items/{item_id}", "GET", 2025, "flaskapp/views.py", 5), // register_blueprint 的 url_prefix 覆盖 Blueprint 的 /api；<int:item_id> → {item_id}
        }, Rows(flask));
    }

    [Fact]
    public void Python_Django_IncludeChain_ClassViews_FunctionViews_AndDrfRouter()
    {
        var entries = PyEntries();
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("conduit", "/api/articles/feed", "GET", 2051, "conduit/apps/articles/views.py", 17),                    // ^api/ + ^articles/feed/?$；list → GET
            ("conduit", "/api/articles/{article_slug}/comments", "GET", 2053, "conduit/apps/articles/views.py", 21), // re_path 正则 → 模板；类视图的 get / post
            ("conduit", "/api/articles/{article_slug}/comments", "POST", 2054, "conduit/apps/articles/views.py", 23),
            ("conduit", "/health/", null, 2042, "conduit/views.py", 1),                                              // 函数视图：方法不定
        }, Rows(entries.Where(e => e.RuleId == "python.django")));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("conduit", "/api/articles", "GET", 2047, "conduit/apps/articles/views.py", 7),         // list
            ("conduit", "/api/articles", "POST", 2046, "conduit/apps/articles/views.py", 4),        // create
            ("conduit", "/api/articles/{slug}", "GET", 2048, "conduit/apps/articles/views.py", 10), // retrieve；lookup_field = 'slug'；trailing_slash=False
        }, Rows(entries.Where(e => e.RuleId == "python.drf")));
        Assert.DoesNotContain(entries, e => e.SymbolId == 2049);                       // _helper
        Assert.DoesNotContain(entries, e => e.Route is not null && e.Route.StartsWith("/admin", StringComparison.Ordinal));
    }

    [Fact]
    public void Python_GrpcServicer_InstanceFromFactory_FallsBackToClassExtendingTheServicer()
    {
        Dictionary<string, string[]> files = new()
        {
            ["email/email_server.py"] =
            [
                "class EmailService(demo_pb2_grpc.EmailServiceServicer):",               // 1
                "    def SendOrderConfirmation(self, request, context):",                // 2
                "        pass",                                                          // 3
                "def start():",                                                          // 4
                "    service = make_service()",                                          // 5：工厂函数，看不出类
                "    demo_pb2_grpc.add_EmailServiceServicer_to_server(service, server)", // 6
            ],
        };
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, "email/email_server.py", "email/email_server.py", null, 1, 6),
            Sym(2, SymbolKind.Class, "email.email_server.EmailService", "email/email_server.py", 1, 1, 3),
            Sym(3, SymbolKind.Method, "email.email_server.EmailService.SendOrderConfirmation", "email/email_server.py", 2, 2, 3),
            Sym(4, SymbolKind.Function, "email.email_server.start", "email/email_server.py", 1, 4, 6),
        ];
        // 生成的 demo_pb2_grpc.py：def add_XxxServicer_to_server(servicer, server) 是定义不是注册；XxxServicer 基类本身不是实现
        files["email/demo_pb2_grpc.py"] =
        [
            "class EmailServiceServicer(object):",                       // 1
            "    def SendOrderConfirmation(self, request, context):",    // 2
            "        raise NotImplementedError()",                       // 3
            "def add_EmailServiceServicer_to_server(servicer, server):", // 4
            "    rpc_method_handlers = {}",                              // 5
        ];
        symbols.AddRange([
            Sym(10, SymbolKind.Module, "email/demo_pb2_grpc.py", "email/demo_pb2_grpc.py", null, 1, 5),
            Sym(11, SymbolKind.Class, "email.demo_pb2_grpc.EmailServiceServicer", "email/demo_pb2_grpc.py", 10, 1, 3),
            Sym(12, SymbolKind.Method, "email.demo_pb2_grpc.EmailServiceServicer.SendOrderConfirmation", "email/demo_pb2_grpc.py", 11, 2, 3),
            Sym(13, SymbolKind.Function, "email.demo_pb2_grpc.add_EmailServiceServicer_to_server", "email/demo_pb2_grpc.py", 10, 4, 5),
        ]);
        var entries = EntryDiscovery.Discover(symbols, [], [Container("email", "python", "email")], p => files.TryGetValue(p, out var l) ? l : []);
        var e = Assert.Single(entries, x => x.RuleId == "python.grpc");
        Assert.Equal(("EmailService/SendOrderConfirmation", 3L, 2), (e.Route, e.SymbolId, e.Line));
    }

    [Fact]
    public void Python_GrpcServicer_Celery_AndMainModules()
    {
        var entries = PyEntries();
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("recommendationservice", "RecommendationService/Check", null, 2063, "recommendationservice/recommendation_server.py", 5),
            ("recommendationservice", "RecommendationService/ListRecommendations", null, 2062, "recommendationservice/recommendation_server.py", 2),
        }, Rows(entries.Where(e => e.RuleId == "python.grpc")));
        Assert.All(entries.Where(e => e.RuleId == "python.grpc"), e => Assert.Equal("grpc", e.Kind));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("flaskapp", "rebuild", null, 2026, "flaskapp/views.py", 9),    // @shared_task(bind=True)
            ("flaskapp", "send_mail", null, 2022, "flaskapp/app.py", 11),   // @celery.task
        }, Rows(entries.Where(e => e.RuleId == "python.celery")));
        Assert.All(entries.Where(e => e.RuleId == "python.celery"), e => Assert.Equal("job", e.Kind));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("flaskapp", "app", null, 2023, "flaskapp/app.py", 14),                                         // 模块里有 main() → 它
            ("recommendationservice", "recommendation_server", null, 2060, "recommendationservice/recommendation_server.py", 10), // 没有 → 落在模块上，行号是 if 那行
        }, Rows(entries.Where(e => e.RuleId == "python.main")));
        Assert.All(entries.Where(e => e.RuleId == "python.main"), e => Assert.Equal("hosted", e.Kind));
    }
}
