namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>
/// 临时目录里的一个 Next.js 前端（照 ccs-ui 的写法缩小）：apiConfig 嵌套对象 + 模板常量 + 导入常量、`@/` 别名与 index.ts 桶文件、
/// useApi → createApiClient → axios.create({ baseURL: PROXY_BASE_PATH }) 的包装、多行调用、Map.get 反例、
/// 用 [...path] 剩余段转发的代理 route.ts、本应用自己的 API 路由（health）与服务端直连后端的 route.ts（chat）。
/// </summary>
public sealed class TsApiFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-tsapi-" + Guid.NewGuid().ToString("N"));

    public TsApiFixture()
    {
        Write("web/package.json", """{ "name": "acme-ui", "scripts": { "build": "next build" }, "dependencies": { "next": "15.0.0", "react": "19.0.0", "axios": "1.7.0" } }""");
        Write("web/tsconfig.json", """
            {
              // JSONC：注释与尾逗号都要能读
              "compilerOptions": {
                "paths": { "@/*": ["./src/*"], },
              },
            }
            """);
        Write("web/src/config/base.ts", "export const V5 = '/api/v5';\n");
        Write("web/src/config/index.ts", "export * from \"./apiConfig\";\n");
        Write("web/src/config/apiConfig.ts", """
            import { V5 } from "./base";
            export const PROXY_BASE_PATH = "/api/proxy-gateway";
            const MT5 = `${V5}/mt5`;
            export const API_ROUTES = {
              CCP_MT5: {
                GROUP: {
                  BASE: `${MT5}/groups`, // 注释
                  DETAIL: (name: string) =>
                    `${V5}/mt5/groups/${name}`,
                  EXPORT: V5 + "/mt5/groups/export",
                },
              },
              "JOBS": { LIST: `${V5}/jobs/list` },
            } as const;
            export const getApiBaseUrl = (): string => {
              return process.env.API_BASE ?? "";
            };
            """);
        Write("web/src/services/apiClient.ts", """
            import axios from "axios";
            import { PROXY_BASE_PATH } from "@/config/apiConfig";
            export class ApiClient {
              private client = axios.create({ baseURL: PROXY_BASE_PATH, timeout: 1000 });
              async get<T>(url: string) { return this.client.get<T>(url); }
            }
            export const createApiClient = () => new ApiClient();
            """);
        Write("web/src/hooks/useApi.ts", """
            import { useMemo } from "react";
            import { createApiClient } from "@/services/apiClient";
            export const useApi = () => useMemo(() => createApiClient(), []);
            """);
        Write("web/src/hooks/useMT5Group.ts", """
            import { useQuery } from "@tanstack/react-query";
            import { useApi } from "@/hooks/useApi";
            import { API_ROUTES } from "@/config";

            export const useMT5Group = (serverId: number) => {
              const api = useApi();
              const cache = new Map<string, number>();
              cache.get("/api/v5/not-http");
              return useQuery({
                queryKey: ["groups", serverId],
                queryFn: async () => {
                  const response = await api.get<IV5APIResponse<IItem[]>>(
                    API_ROUTES.CCP_MT5.GROUP.BASE,
                    { params: { serverId } }
                  );
                  return response;
                },
              });
            };

            export const useMT5GroupMutations = (name: string) => {
              const api = useApi();
              const remove = () => api.delete(API_ROUTES.CCP_MT5.GROUP.DETAIL(name));
              const exportAll = () => api.post(`${API_ROUTES.CCP_MT5.GROUP.EXPORT}?serverId=${name}`, {});
              const url = API_ROUTES.JOBS.LIST;
              const jobs = () => api.put(url, {});
              // api.get(API_ROUTES.CCP_MT5.GROUP.BASE) 注释里的不算
              const missing = () => api.patch(API_ROUTES.CCP_MT5.NOPE.X);
              return { remove, exportAll, jobs, missing };
            };
            """);
        Write("web/src/services/realtime.ts", """
            import { PROXY_BASE_PATH } from "@/config/apiConfig";
            const TICKET = `${PROXY_BASE_PATH}/api/v5/realtime/ticket`;
            export const ticket = () => fetch(TICKET, { method: "POST", cache: "no-store" });
            export const health = () => fetch("/api/health");
            """);
        Write("web/src/app/api/proxy-gateway/[...path]/route.ts", """
            import axios from "axios";
            import { NextRequest } from "next/server";
            async function handle(request: NextRequest) {
              const proxyPath = request.nextUrl.pathname.replace("/api/proxy-gateway", "");
              return axios.request({ baseURL: process.env.BACKEND, url: proxyPath, method: request.method });
            }
            export const GET = handle;
            export const POST = handle;
            """);
        // 剩余段路由，但转发交给库函数（ccs-ui 的 knowledge / chat-state）：本文件里没有出站调用 → 不算代理
        Write("web/src/app/api/knowledge/[[...path]]/route.ts", """
            import { createKnowledgeProxyRoute } from "@acme/chatbot/server";
            export const { GET, POST } = createKnowledgeProxyRoute({ base: "/api/knowledge" });
            """);
        Write("web/src/app/api/health/route.ts", "export const GET = () => Response.json({ ok: true });\n");
        Write("web/src/app/api/chat/route.ts", """
            import { getApiBaseUrl } from "@/config/apiConfig";
            export async function POST() {
              const baseUrl = getApiBaseUrl();
              const res = await fetch(`${baseUrl}/api/v5/system/brands?mtVersion=4`, { headers: {} });
              return res;
            }
            """);
        Write("web/src/hooks/useApi.test.ts", "const api = useApi();\napi.get(\"/api/v5/from-test\");\n");
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
