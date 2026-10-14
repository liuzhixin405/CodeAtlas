using System.Net;
using Microsoft.AspNetCore.Builder;

namespace CodeAtlas.Server.Tests;

public class AuthTests
{
    [Fact]
    public async Task WithToken_RequestsWithoutHeaderGet401_WithHeaderSucceed_PreflightStaysOpen()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codeatlas-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "a.ts"), "export function a() {}\n");
        WebApplication? app = null;
        try
        {
            app = ServerHost.Build(new ServerOptions(repo, ServerOptions.DefaultDbPath(repo)) { Token = "s3cret" });
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/health")).StatusCode);

            using var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/health");
            wrong.Headers.Add("X-CodeAtlas-Token", "nope");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);

            using var ok = new HttpRequestMessage(HttpMethod.Get, "/api/health");
            ok.Headers.Add("X-CodeAtlas-Token", "s3cret");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ok)).StatusCode);

            // 浏览器预检不带自定义头，必须放行，否则带 token 的真实请求永远发不出去
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/health");
            preflight.Headers.Add("Origin", "vscode-webview://abc");
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            preflight.Headers.Add("Access-Control-Request-Headers", "x-codeatlas-token");
            var p = await client.SendAsync(preflight);
            Assert.Equal(HttpStatusCode.NoContent, p.StatusCode);
            Assert.Contains("x-codeatlas-token", p.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
            Directory.Delete(repo, recursive: true);
        }
    }
}
