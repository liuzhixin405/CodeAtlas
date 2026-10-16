using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>
/// JWT / OIDC 的身份提供方：AddJwtBearer / AddOpenIdConnect 里的 <c>options.Authority = x</c> → 协议 oidc 的出口。
/// x 解析成 URL 或配置键（字面量、configuration["A:B"]、GetValue&lt;T&gt;("A:B")、section.GetRequiredValue("Url") 配 GetSection("Identity")、局部变量往回找）；
/// 地址对上本仓库的容器（eShop 的 Identity.API）就连容器，否则挂 identity 外部系统，名字按地址 / 上下文认（/realms/ 或 keycloak → Keycloak，Auth0、Okta、Cognito、Entra ID）。
/// </summary>
public sealed class IdentityProviderRule : IDiscoveryRule
{
    public const string RuleId = "identity-provider";
    public string Id => RuleId;
    public string Description => "AddJwtBearer / AddOpenIdConnect 的 Authority（身份提供方：Keycloak、IdentityServer、Auth0…）";

    private static readonly Regex Authority = new(@"\bAuthority\s*=\s*([^;,]+?)\s*[;,]?\s*$", RegexOptions.Compiled);
    private static readonly Regex AuthContext = new(@"\bAdd(?:JwtBearer|OpenIdConnect|IdentityServerAuthentication|MicrosoftIdentityWebApi)\b|\b(?:JwtBearerOptions|OpenIdConnectOptions)\b", RegexOptions.Compiled);
    private static readonly Regex UrlLiteral = new(@"""(https?://[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex Indexer = new(@"\[\s*""([^""]+)""\s*\]", RegexOptions.Compiled);
    private static readonly Regex GetValue = new(@"(?:(\w+)\s*\.\s*)?(?:GetValue<[^>]+>|GetRequiredValue|GetConnectionString)\s*\(\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex SectionAssign = new(@"\b(\w+)\s*=\s*[\w.]*GetSection\(\s*""([^""]+)""\s*\)", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"^@?([A-Za-z_]\w*)(?:\.ToString\(\))?$", RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Name)[] Providers =
    [
        (new(@"keycloak|key_?c?lo[ac]k|/realms/", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Keycloak"),
        (new(@"auth0", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Auth0"),
        (new(@"okta", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Okta"),
        (new(@"cognito", RegexOptions.IgnoreCase | RegexOptions.Compiled), "AWS Cognito"),
        (new(@"login\.microsoftonline|AzureAd|EntraId", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Microsoft Entra ID"),
    ];

    public void Apply(RuleContext ctx)
    {
        foreach (var file in ctx.CSharpFiles())
        {
            var lines = ctx.Lines(file);
            if (!Array.Exists(lines, l => l.Contains("Authority", StringComparison.Ordinal)) || !Array.Exists(lines, l => AuthContext.IsMatch(l))) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            var code = SourceMask.Code(lines, SourceLang.CSharp);
            var text = SourceMask.WithoutComments(lines, SourceLang.CSharp);
            for (var i = 0; i < lines.Length; i++)
            {
                var m = Authority.Match(text[i]);
                if (!m.Success || char.IsWhiteSpace(code[i][m.Index])) continue;
                // 认证配置的上下文（Authority 这个名字别处也用）：往上 25 行内有 AddJwtBearer / AddOpenIdConnect，
                // 且 x.Authority 的 x 是那里 lambda 的参数（options => …）；对象初始化器 Authority = … 要紧挨着（3 行内）
                var receiver = Regex.Match(code[i][..m.Index], @"(\w+)\s*\.\s*$");
                var from = Math.Max(0, i - (receiver.Success ? 25 : 3));
                var ctxLine = Enumerable.Range(from, i - from + 1).Reverse().FirstOrDefault(k => AuthContext.IsMatch(code[k]), -1);
                if (ctxLine < 0) continue;
                if (receiver.Success)
                {
                    var param = new Regex(@"\b" + Regex.Escape(receiver.Groups[1].Value) + @"\s*\)?\s*=>");
                    if (!Enumerable.Range(ctxLine, Math.Min(3, i - ctxLine + 1)).Any(k => param.IsMatch(code[k]))) continue;
                }
                var (url, configKey) = Resolve(m.Groups[1].Value.Trim(), text, i, 0);
                var configured = configKey is not null ? ctx.Inventory.Config(ctx.ProjectDirOf(file) ?? "", configKey) : null;
                var window = string.Join('\n', lines[Math.Max(0, i - 25)..Math.Min(lines.Length, i + 6)]);
                var name = Providers.FirstOrDefault(p => p.Pattern.IsMatch((url ?? "") + "\n" + (configured ?? "") + "\n" + (configKey ?? "") + "\n" + window)).Name;
                foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, RuleId, "oidc", name, configKey, url, file, i + 1, null));
            }
        }
    }

    /// <summary>Authority 右边的表达式 → (URL, 配置键)；局部变量在前 40 行里找赋值，最多追两层。</summary>
    private static (string? Url, string? ConfigKey) Resolve(string expr, string[] text, int line, int depth)
    {
        if (UrlLiteral.Match(expr) is { Success: true } u) return (u.Groups[1].Value, null);
        if (GetValue.Match(expr) is { Success: true } g)
        {
            var key = g.Groups[2].Value;
            // section.GetRequiredValue("Url")：section = configuration.GetSection("Identity") → Identity:Url
            if (g.Groups[1].Success && SectionOf(g.Groups[1].Value, text, line) is { } section) key = section + ":" + key;
            return (null, key);
        }
        if (Indexer.Match(expr) is { Success: true } ix) return (null, ix.Groups[1].Value);
        if (depth >= 2 || Identifier.Match(expr) is not { Success: true } id) return (null, null);
        var assign = new Regex(@"\b" + Regex.Escape(id.Groups[1].Value) + @"\s*=\s*([^;=][^;]*);");
        for (var k = line - 1; k >= 0 && k >= line - 40; k--)
            if (assign.Match(text[k]) is { Success: true } a) return Resolve(a.Groups[1].Value.Trim(), text, k, depth + 1);
        return (null, null);
    }

    private static string? SectionOf(string variable, string[] text, int line)
    {
        for (var k = line; k >= 0 && k >= line - 40; k--)
            if (SectionAssign.Match(text[k]) is { Success: true } s && s.Groups[1].Value == variable) return s.Groups[2].Value;
        return null;
    }
}
