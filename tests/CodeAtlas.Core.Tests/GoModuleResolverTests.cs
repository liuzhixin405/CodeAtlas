using CodeAtlas.Core.Resolution;

namespace CodeAtlas.Core.Tests;

public class GoModuleResolverTests
{
    [Fact]
    public void ParseGoMod_ReadsModulePath_AndOnlyLocalReplaces()
    {
        var m = GoModuleResolver.ParseGoMod("services/api", """
            // 注释
            module github.com/acme/api // 行尾注释

            go 1.22

            require github.com/gin-gonic/gin v1.10.0

            replace github.com/acme/shared => ../shared
            replace (
            	github.com/acme/proto v1.0.0 => ./gen/proto
            	github.com/other/fork => github.com/me/fork v1.2.3
            )
            """);
        Assert.NotNull(m);
        Assert.Equal(("services/api", "github.com/acme/api"), (m.Directory, m.ModulePath));
        Assert.Equal(new[]
        {
            new KeyValuePair<string, string>("github.com/acme/shared", "services/shared"),
            new KeyValuePair<string, string>("github.com/acme/proto", "services/api/gen/proto"),
        }, m.Replaces);
        Assert.Null(GoModuleResolver.ParseGoMod("", "go 1.22\n")); // 没有 module 行
    }

    [Fact]
    public void Resolve_ModulePrefix_Replace_SiblingModule_Vendor_OtherwiseNull()
    {
        var modules = new[]
        {
            GoModuleResolver.ParseGoMod("", "module example.com/root\nreplace example.com/lib => ./third/lib\n")!,
            GoModuleResolver.ParseGoMod("svc", "module example.com/svc\n")!,
        };
        var dirs = new HashSet<string>(StringComparer.Ordinal) { "", "pkg/a", "third/lib/x", "svc", "svc/h", "svc/vendor/github.com/v/w", "cart" };
        var r = new GoModuleResolver(modules, dirs);

        Assert.Equal("pkg/a", r.Resolve("main.go", "example.com/root/pkg/a"));
        Assert.Equal("", r.Resolve("pkg/a/a.go", "example.com/root"));
        Assert.Equal("third/lib/x", r.Resolve("main.go", "example.com/lib/x"));       // replace 到本地目录
        Assert.Equal("svc/h", r.Resolve("svc/main.go", "example.com/svc/h"));          // 最近的 go.mod 生效
        Assert.Equal("cart", r.Resolve("svc/main.go", "example.com/root/cart"));       // 同仓库另一个模块
        Assert.Equal("svc/vendor/github.com/v/w", r.Resolve("svc/main.go", "github.com/v/w")); // vendor/
        Assert.Null(r.Resolve("main.go", "net/http"));                                  // 标准库
        Assert.Null(r.Resolve("main.go", "example.com/root/missing"));                  // 本模块但目录不在索引里
        Assert.Null(r.Resolve("main.go", "github.com/v/w"));                            // vendor 只对所属模块生效
    }

    [Fact]
    public void Resolve_WithoutGoMod_FallsBackToLongestDirectorySuffix()
    {
        var r = new GoModuleResolver([], new HashSet<string>(StringComparer.Ordinal) { "users", "common", "x/users" });
        Assert.Equal("x/users", r.Resolve("main.go", "github.com/acme/app/x/users"));
        Assert.Equal("common", r.Resolve("main.go", "github.com/acme/app/common"));
        Assert.Null(r.Resolve("main.go", "github.com/gin-gonic/gin"));
    }

    [Theory]
    [InlineData("net/http", "http")]
    [InlineData("fmt", "fmt")]
    [InlineData("github.com/golang-jwt/jwt/v5", "jwt")]
    [InlineData("gopkg.in/yaml.v3", "yaml")]
    [InlineData("github.com/mattn/go-sqlite3", "sqlite3")]
    [InlineData("gorm.io/gorm", "gorm")]
    public void DefaultPackageName_FollowsGoimportsConvention(string importPath, string expected) =>
        Assert.Equal(expected, GoModuleResolver.DefaultPackageName(importPath));

    [Theory]
    [InlineData("func NewStore() *Store {", null, "Store")]
    [InlineData("func NewProductCatalogServiceClient(cc grpc.ClientConnInterface) ProductCatalogServiceClient {", null, "ProductCatalogServiceClient")]
    [InlineData("func Open(dialector Dialector, opts ...Option) (db *DB, err error) {", null, "DB")]
    [InlineData("func (s *Server) Client() (*pb.CartClient, error) {", "pb", "CartClient")]
    [InlineData("func New[T any](xs []T) *List[T] { return nil }", null, "List")]
    // []Item 原先返回 null；现在取核心类型 Item：切片本身没有方法，range / 下标取到的元素就是 Item
    [InlineData("func Items() []Item {", null, "Item")]
    [InlineData("func Index() (map[string]*cart.Item, error) {", "cart", "Item")]
    [InlineData("func Run() {", null, null)]
    [InlineData("func NewServer(", null, null)]
    [InlineData("GetCart(ctx context.Context, in *GetCartRequest, opts ...grpc.CallOption) (*Cart, error)", null, "Cart")] // 接口方法
    [InlineData("func(ctx context.Context) *gorm.DB", "gorm", "DB")]                                                      // 函数类型（字段 / 变量）
    [InlineData("func (s *Server) Watch() <-chan Event {", null, "Event")]
    [InlineData("func Handler() func(w http.ResponseWriter) {", null, null)]
    public void FirstResultType_ParsesSignatureLine(string signature, string? qualifier, string? name)
    {
        var t = GoModuleResolver.FirstResultType(signature);
        Assert.Equal((qualifier, name), (t?.Qualifier, t?.Name));
    }

    [Theory]
    [InlineData("*gorm.DB", "gorm", "DB")]
    [InlineData("map[string]*cart.Cart", "cart", "Cart")]
    [InlineData("[]*CartItem", null, "CartItem")]
    [InlineData("[4]Item", null, "Item")]
    [InlineData("chan<- Job", null, "Job")]
    [InlineData("List[T]", null, "List")]
    [InlineData("...Option", null, "Option")]
    [InlineData("func()", null, null)]
    [InlineData("struct{ a int }", null, null)]
    public void CoreType_StripsContainers(string type, string? qualifier, string? name)
    {
        var t = GoModuleResolver.CoreType(type);
        Assert.Equal((qualifier, name), (t?.Qualifier, t?.Name));
    }
}
