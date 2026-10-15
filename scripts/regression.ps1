# CodeAtlas 交付前回归：十四个"单独打开 / 合并打开"的真实项目场景（C# / TS / Go / Python / Java），逐项判定并打印结果表。
# 用法：powershell -File scripts\regression.ps1 [-Work <临时目录>] [-Only 1,5] [-NoRoslyn]
# 依赖：dist\codeatlas.exe（先跑 scripts\publish.ps1）；ccs-backend 与 eShop 的本机路径见下面的 $Repos。
param(
    [string]$Work = (Join-Path $env:TEMP 'codeatlas-regression'),
    [int[]]$Only = @(),
    [switch]$NoRoslyn
)
$ErrorActionPreference = 'Continue'
$Exe = Join-Path $PSScriptRoot '..\dist\codeatlas.exe' | Resolve-Path | Select-Object -ExpandProperty Path
$Repos = @{
    CcsBackend = 'D:\gitlab\RC_SZ\ccs-backend'
    Mt4Proxy   = 'D:\gitlab\RC_SZ\ccs-backend\mt4proxy'
    Mt5Pub     = 'D:\gitlab\RC_SZ\ccs-backend\mt5-event-publisher'
    CcsUi      = 'D:\gitlab\RC_SZ\ccs-backend\ccs-ui'
    EShop      = 'D:\github\reference\eShop'
    Boutique   = 'D:\github\reference\microservices-demo'
    FastApi    = 'D:\github\reference\full-stack-fastapi-template'
    GinRw      = 'D:\github\reference\golang-gin-realworld-example-app'
    DjangoRw   = 'D:\github\reference\django-realworld-example-app'
    PetClinic  = 'D:\github\reference\spring-petclinic'
    PetMicro   = 'D:\github\reference\spring-petclinic-microservices'
    CodeAtlas  = (Join-Path $PSScriptRoot '..' | Resolve-Path | Select-Object -ExpandProperty Path)
    Bing       = 'D:\github\Bing.NetCore-master'
}
New-Item -ItemType Directory -Force $Work | Out-Null
$results = New-Object System.Collections.Generic.List[object]

function Invoke-Ca([string[]]$CaArgs) {
    $out = & $Exe @CaArgs 2>&1 | ForEach-Object { "$_" }
    return ,$out
}

function Index-Repo([string]$Name, [string]$Root, [string[]]$Extra) {
    $db = Join-Path $Work "$Name\index.db"
    $roslyn = if ($NoRoslyn) { 'off' } else { 'auto' }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = Invoke-Ca (@('index', $Root, '--db', $db, '--full', '--roslyn', $roslyn) + $Extra)
    $sw.Stop()
    $out | Out-File (Join-Path $Work "$Name\index.log") -Encoding utf8
    return @{ Db = $db; Seconds = [int]$sw.Elapsed.TotalSeconds; Log = $out }
}

function Get-Json([string[]]$CaArgs) {
    $text = (Invoke-Ca $CaArgs) -join "`n"
    $start = $text.IndexOfAny([char[]]'[{')
    if ($start -lt 0) { return $null }
    try { return $text.Substring($start) | ConvertFrom-Json } catch { return $null }
}

function Get-Entries($Root, $Db, $Container, $Kind, $Q, [string[]]$Extra) {
    $a = @('arch', $Root, '--db', $Db, '--entries', $Container, '--json') + $Extra
    if ($Kind) { $a += @('--kind', $Kind) }
    if ($Q) { $a += @('--q', $Q) }
    $r = Get-Json $a
    if ($null -eq $r) { return @() } else { return @($r) }
}

function Get-FlowNodes($Root, $Db, $Id, [string[]]$Extra) {
    $flow = Get-Json (@('arch', $Root, '--db', $Db, '--flow', "$Id", '--json') + $Extra)
    $nodes = New-Object System.Collections.Generic.List[object]
    function Walk($n) { if ($null -eq $n) { return }; $nodes.Add($n); foreach ($c in @($n.children)) { Walk $c } }
    if ($flow) { Walk $flow.root }
    return @{ Flow = $flow; Nodes = $nodes }
}

function Check([int]$Scenario, [string]$Item, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ 场景 = $Scenario; 检查 = $Item; 结果 = $(if ($Ok) { '通过' } else { '不通过' }); 实测 = $Detail })
}

# 命令行 --json 里节点的 kind 是枚举数值（0 = 容器）；人读输出里是 container
function Is-Container($n) { return $n.kind -eq 0 -or "$($n.kind)" -eq 'container' }

function Want([int]$n) { return $Only.Count -eq 0 -or $Only -contains $n }

function Line-Value($Log, $Pattern) { $m = $Log | Select-String -Pattern $Pattern | Select-Object -First 1; if ($m) { return $m.Matches[0].Groups[1].Value } else { return '' } }

# 跨服务链：组管理页 → … → 后端 Mt5GroupController（前端与后端在同一个索引里时）
function Check-GroupsChain([int]$S, $Root, $Db, $UiContainer, [string[]]$Extra) {
    $page = Get-Entries $Root $Db $UiContainer 'page' '/groups' $Extra | Where-Object { $_.route -match '/groups$' } | Select-Object -First 1
    if (-not $page) { Check $S '组管理页入口' $false '没找到 …/groups 页面入口'; return }
    $f = Get-FlowNodes $Root $Db $page.symbolId $Extra
    $labels = $f.Nodes | ForEach-Object { $_.label }
    $hops = @($f.Nodes | Where-Object { $_.kind -eq 'hop' })
    Check $S '组管理页 → 组件 → hook' (($labels -contains 'MT5GroupTable') -and (@($labels | Where-Object { $_ -like '*useMT5GroupByServerId*' }).Count -gt 0)) "节点 $($f.Nodes.Count)，含 MT5GroupTable：$($labels -contains 'MT5GroupTable')"
    $intoBackend = @($f.Nodes | Where-Object { $_.label -like '*Mt5GroupController*' -or $_.qualifiedName -like '*Mt5GroupController*' })
    Check $S '前端续接进后端控制器' ($intoBackend.Count -gt 0) "hop $($hops.Count) 个$(if ($hops.Count) { '，首个：' + $hops[0].label + ' ' + $hops[0].note })；Mt5GroupController 节点 $($intoBackend.Count) 个"
}

# ---------- 1. ccs-backend（含三个嵌套仓库）----------
if (Want 1) {
    $ix = Index-Repo 'ccs-backend' $Repos.CcsBackend @()
    $files = Line-Value $ix.Log '^files: (\d+)'
    Check 1 '索引（含嵌套仓库）' ([int]$files -gt 6000) "文件 $files，$($ix.Seconds)s"
    $batch = Get-Entries $Repos.CcsBackend $ix.Db 'CentralizedConfigurationSystem.Api' 'http' 'mt5/groups/batch-create' @() | Select-Object -First 1
    if ($batch) {
        $f = Get-FlowNodes $Repos.CcsBackend $ix.Db $batch.symbolId @()
        $labels = $f.Nodes | ForEach-Object { $_.label }
        Check 1 'MT5 批量建组流程' (($f.Nodes.Count -ge 30) -and (@($labels | Where-Object { $_ -like '*SendAndReadDataAsync*' }).Count -gt 0)) "节点 $($f.Nodes.Count)，走到 SendAndReadDataAsync：$(@($labels | Where-Object { $_ -like '*SendAndReadDataAsync*' }).Count -gt 0)"
    } else { Check 1 'MT5 批量建组流程' $false '没找到入口' }
    $mt4 = Get-Entries $Repos.CcsBackend $ix.Db 'mt4proxy/MT4Proxy.Web' 'http' $null @()
    Check 1 '嵌套仓库 mt4proxy 有入口' ($mt4.Count -ge 70) "http 入口 $($mt4.Count)"
    Check-GroupsChain 1 $Repos.CcsBackend $ix.Db 'ccs-ui/ccs-src/ccs-app' @()
}

# ---------- 2. mt4proxy 单独打开 ----------
if (Want 2) {
    $ix = Index-Repo 'mt4proxy' $Repos.Mt4Proxy @()
    $e = Get-Entries $Repos.Mt4Proxy $ix.Db 'MT4Proxy.Web' 'http' 'manager-group-logins' @() | Select-Object -First 1
    if ($e) {
        $f = Get-FlowNodes $Repos.Mt4Proxy $ix.Db $e.symbolId @()
        $p23 = @($f.Flow.actors | Where-Object { $_.label -eq 'P23' }).Count -gt 0
        Check 2 '控制器 → Service → P23 原生接口' (($f.Nodes.Count -ge 30) -and $p23) "节点 $($f.Nodes.Count)，到 P23：$p23，$($ix.Seconds)s"
    } else { Check 2 '控制器 → Service → P23 原生接口' $false '没找到入口' }
}

# ---------- 3. mt5-event-publisher 单独打开 ----------
if (Want 3) {
    $ix = Index-Repo 'mt5pub' $Repos.Mt5Pub @()
    $jobs = Get-Entries $Repos.Mt5Pub $ix.Db 'Publisher' 'job' $null @()
    Check 3 'Quartz Job 入口' ($jobs.Count -ge 3) "job $($jobs.Count) 个，$($ix.Seconds)s"
    # 设计稿原写"一条 Job 流程以 Kafka 收尾"，核实后不成立：Jobs 目录里没有 Kafka；生产 Kafka 的是常驻服务 StatusEventPublisher
    $hosted = Get-Entries $Repos.Mt5Pub $ix.Db 'Publisher' 'hosted' $null @()
    $kafka = $false; $detail = ''
    foreach ($j in @($jobs) + @($hosted)) {
        $f = Get-FlowNodes $Repos.Mt5Pub $ix.Db $j.symbolId @()
        $hit = @($f.Nodes | Where-Object { $_.label -match 'Kafka|Produce' -or $_.qualifiedName -match 'Kafka' }).Count
        $detail += "$($j.name) $($f.Nodes.Count) 节点 / Kafka $hit；"
        if ($hit -gt 0) { $kafka = $true }
    }
    Check 3 '有入口流程走到 Kafka 生产' $kafka $detail
}

# ---------- 4. eShop（外部项目）----------
if (Want 4) {
    $ix = Index-Repo 'eshop' $Repos.EShop @()
    $matches = Line-Value $ix.Log 'route-matches: (\d+)'
    Check 4 '跨服务路由匹配' ([int]$matches -ge 20) "route-matches $matches，$($ix.Seconds)s"
    $e = Get-Entries $Repos.EShop $ix.Db 'src/Ordering.API' 'http' '/api/orders' @() | Where-Object { $_.httpMethod -eq 'POST' -and $_.route -eq '/api/orders' } | Select-Object -First 1
    if ($e) {
        $f = Get-FlowNodes $Repos.EShop $ix.Db $e.symbolId @()
        $disp = @($f.Flow.root.children | Where-Object { $_.kind -eq 'dispatch' } | ForEach-Object { $_.label })
        Check 4 '下单只分派到对应 Handler' (($disp.Count -ge 1) -and (@($disp | Where-Object { $_ -notlike '*CreateOrder*' }).Count -eq 0)) ("分派：" + ($disp -join '、'))
    } else { Check 4 '下单只分派到对应 Handler' $false '没找到 POST /api/orders' }
    # 调用大纲：按源码顺序、foreach 里的 AddOrderItem 标循环内（开 Roslyn 时限定名带参数，不开时不带，两种都试）
    $handler = 'eShop.Ordering.API.Application.Commands.CreateOrderCommandHandler.Handle'
    $outline = Invoke-Ca @('query', $Repos.EShop, "$handler(CreateOrderCommand, CancellationToken)", '--db', $ix.Db, '--outline')
    if (-not ($outline -match 'AddOrderItem')) { $outline = Invoke-Ca @('query', $Repos.EShop, $handler, '--db', $ix.Db, '--outline') }
    # 顶层步骤行："  5. + call      Order.AddOrderItem  @ L44 [loop]"（+ 表示可展开，可能没有）
    $steps = @($outline | ForEach-Object { if ($_ -match '^\s{2}\d+\.\s+\+?\s*\S+\s+(\S+)') { $Matches[1] } })
    $want = @('IOrderingIntegrationEventService.AddAndSaveEventAsync', 'Address', 'Order', 'Order.AddOrderItem', 'IOrderRepository.Add', 'IUnitOfWork.SaveEntitiesAsync')
    $inOrder = (@($steps | Where-Object { $want -contains $_ }) -join ',') -eq ($want -join ',')
    $loop = @($outline | Where-Object { $_ -match 'Order\.AddOrderItem.*\[loop\]' }).Count -gt 0
    Check 4 '调用大纲按源码顺序、标出循环内' ($inOrder -and $loop) ("步骤：" + ($steps -join ' → ') + "；AddOrderItem 循环内：$loop")
    # 影响范围：AddOrderItem 往上经两次分派走到 POST /api/orders
    $add = 'eShop.Ordering.Domain.AggregatesModel.OrderAggregate.Order.AddOrderItem'
    $impact = Invoke-Ca @('query', $Repos.EShop, "$add(int, string, decimal, decimal, string, int)", '--db', $ix.Db, '--impact')
    if (-not ($impact -match '入口')) { $impact = Invoke-Ca @('query', $Repos.EShop, $add, '--db', $ix.Db, '--impact') }
    $post = @($impact | Where-Object { $_ -match 'HTTP POST /api/orders\s+←\s*深度 (\d+)' }) | Select-Object -First 1
    Check 4 '影响范围追到 POST /api/orders' ($null -ne $post) ("入口：" + (@($impact | Where-Object { $_ -match '←\s*深度' } | ForEach-Object { $_.Trim() }) -join '；'))
    # 模块关系：组件按依赖序，领域 → 基础设施 → API
    $rel = Invoke-Ca @('query', $Repos.EShop, 'eShop.Ordering.API.Application.Commands.CreateOrderCommandHandler', '--db', $ix.Db, '--relations')
    $comps = @($rel | ForEach-Object { if ($_ -match '^\s{2}\d+\.\s+(\S+)') { $Matches[1] } })
    $iD = [array]::IndexOf($comps, 'Ordering.Domain'); $iI = [array]::IndexOf($comps, 'Ordering.Infrastructure'); $iA = [array]::IndexOf($comps, 'Ordering.API')
    Check 4 '模块关系组件按依赖序' (($iD -ge 0) -and ($iD -lt $iI) -and ($iI -lt $iA)) ("组件：" + ($comps -join ' → '))
}

# ---------- 5. ccs-ui 单独打开 ----------
if (Want 5) {
    $ix = Index-Repo 'ccs-ui' $Repos.CcsUi @()
    $out = Invoke-Ca @('arch', $Repos.CcsUi, '--db', $ix.Db)
    $outbound = Line-Value $out '出口 (\d+)'
    Check 5 '前端接口调用识别' ([int]$outbound -ge 300) "出口 $outbound，$($ix.Seconds)s"
    $page = Get-Entries $Repos.CcsUi $ix.Db 'ccs-src/ccs-app' 'page' '/groups' @() | Where-Object { $_.route -match '/groups$' } | Select-Object -First 1
    if ($page) {
        $f = Get-FlowNodes $Repos.CcsUi $ix.Db $page.symbolId @()
        $labels = $f.Nodes | ForEach-Object { $_.label }
        Check 5 '组管理页 → 组件 → hook' (($f.Nodes.Count -ge 30) -and ($labels -contains 'MT5GroupTable') -and (@($labels | Where-Object { $_ -like '*useMT5GroupByServerId*' }).Count -gt 0)) "节点 $($f.Nodes.Count)，根 $($f.Flow.root.label)"
        # 后端不在索引里时，hook 下面也要画出它调的接口（未对上后端），不能停在"N 个框架调用"
        $http = @($f.Nodes | Where-Object { $_.kind -eq 'http' -and $_.label -eq 'GET /api/v5/mt5/groups' })
        Check 5 'hook 下显示接口调用' ($http.Count -gt 0) "kind=http 节点 $(@($f.Nodes | Where-Object { $_.kind -eq 'http' }).Count) 个，含 GET /api/v5/mt5/groups：$($http.Count -gt 0)"
    } else { Check 5 '组管理页 → 组件 → hook' $false '没找到页面入口' }
}

# ---------- 6. ccs-ui + 外部根 ccs-backend ----------
if (Want 6) {
    $extra = @('--project', $Repos.CcsBackend)
    $ix = Index-Repo 'ccs-ui+backend' $Repos.CcsUi $extra
    $out = Invoke-Ca (@('arch', $Repos.CcsUi, '--db', $ix.Db) + $extra)
    $containers = Line-Value $out '^容器 (\d+)'
    Check 6 '前后端五个容器' ([int]$containers -ge 5) "容器 $containers，$($ix.Seconds)s"
    Check-GroupsChain 6 $Repos.CcsUi $ix.Db 'ccs-src/ccs-app' $extra
}


# ---------- 7. Online Boutique（Go + Python + C#，gRPC）----------
if (Want 7) {
    $ix = Index-Repo 'boutique' $Repos.Boutique @()
    $arch = Get-Json @('arch', $Repos.Boutique, '--db', $ix.Db, '--json')
    $containers = @($arch.nodes | Where-Object { Is-Container $_ })
    $grpc = @($arch.edges | Where-Object { $_.protocol -eq 'grpc' -and $_.confidence -ge 0.9 })
    Check 7 '多语言容器（含 Java adservice）' (($containers.Count -ge 12) -and (@($containers | Where-Object { $_.subKind -eq 'java-service' }).Count -ge 1)) "容器 $($containers.Count)：$(($containers | ForEach-Object { $_.name }) -join '、')，$($ix.Seconds)s"
    Check 7 'gRPC 按服务名连线' ($grpc.Count -ge 14) "0.9 的 grpc 边 $($grpc.Count) 条"
    $fe = $containers | Where-Object { $_.name -eq 'frontend' } | Select-Object -First 1
    if ($fe) {
        $root = Get-Entries $Repos.Boutique $ix.Db $fe.key 'http' $null @() | Where-Object { $_.route -eq '/' } | Select-Object -First 1
        if ($root) {
            $f = Get-FlowNodes $Repos.Boutique $ix.Db $root.symbolId @()
            $hop = @($f.Nodes | Where-Object { $_.kind -eq 'hop' -and ($_.label -match 'ListProducts|ProductCatalog' -or $_.note -match 'productcatalog') })
            Check 7 'Go 首页 → gRPC 续接进商品目录服务' ($hop.Count -gt 0) "节点 $($f.Nodes.Count)，续接 $($hop.Count) 个"
            $ad = @($f.Nodes | Where-Object { $_.label -like '*getAdsByCategory*' -or $_.label -like '*AdServiceImpl.getAds*' })
            Check 7 'Go 首页 → gRPC 续接进 Java 广告服务' ($ad.Count -gt 0) "AdServiceImpl 节点 $($ad.Count) 个"
            $pay = $containers | Where-Object { $_.name -eq 'paymentservice' } | Select-Object -First 1
            # PowerShell 5.1：if 表达式会把单元素数组拆成对象，而单个 PSCustomObject 没有 .Count，所以外面再包一层 @()
            $charge = @(if ($pay) { Get-Entries $Repos.Boutique $ix.Db $pay.key 'grpc' $null @() | Where-Object { $_.route -eq 'PaymentService/Charge' } })
            Check 7 'Node gRPC 服务端入口' ($charge.Count -gt 0) "paymentservice 的 PaymentService/Charge：$($charge.Count -gt 0)"
        } else { Check 7 'Go 首页 → gRPC 续接进商品目录服务' $false '没找到 frontend 的 / 入口' }
    } else { Check 7 'Go 首页 → gRPC 续接进商品目录服务' $false '没认出 frontend 容器' }
}

# ---------- 8. FastAPI 全栈模板（Python + React）----------
if (Want 8) {
    $ix = Index-Repo 'fastapi' $Repos.FastApi @()
    $matches = Line-Value $ix.Log 'route-matches: (\d+)'
    Check 8 '前端 → FastAPI 路由匹配' ([int]$matches -ge 10) "route-matches $matches，$($ix.Seconds)s"
    $arch = Get-Json @('arch', $Repos.FastApi, '--db', $ix.Db, '--json')
    $be = @($arch.nodes | Where-Object { (Is-Container $_) -and $_.subKind -eq 'python-service' }) | Select-Object -First 1
    if ($be) {
        $http = Get-Entries $Repos.FastApi $ix.Db $be.key 'http' $null @()
        $v1 = @($http | Where-Object { $_.route -like '/api/v1/*' })
        Check 8 'FastAPI 入口带前缀' ($v1.Count -ge 15) "http $($http.Count)，/api/v1/* $($v1.Count)"
    } else { Check 8 'FastAPI 入口带前缀' $false '没认出 backend 容器' }
}

# ---------- 9. gin RealWorld（Go）----------
if (Want 9) {
    $ix = Index-Repo 'gin-rw' $Repos.GinRw @()
    $arch = Get-Json @('arch', $Repos.GinRw, '--db', $ix.Db, '--json')
    $c = @($arch.nodes | Where-Object { (Is-Container $_) -and $_.subKind -eq 'go-service' }) | Select-Object -First 1
    if ($c) {
        $http = Get-Entries $Repos.GinRw $ix.Db $c.key 'http' $null @()
        Check 9 'gin 路由入口' ($http.Count -ge 10) "http $($http.Count)，样例 $((@($http | Select-Object -First 3) | ForEach-Object { "$($_.httpMethod) $($_.route)" }) -join '；')，$($ix.Seconds)s"
        $e = $http | Select-Object -First 1
        if ($e) { $f = Get-FlowNodes $Repos.GinRw $ix.Db $e.symbolId @(); Check 9 'gin handler 流程' ($f.Nodes.Count -ge 3) "$($e.httpMethod) $($e.route)：$($f.Nodes.Count) 节点" }
    } else { Check 9 'gin 路由入口' $false '没认出 Go 容器' }
}

# ---------- 10. Django RealWorld（Python）----------
if (Want 10) {
    $ix = Index-Repo 'django-rw' $Repos.DjangoRw @()
    $arch = Get-Json @('arch', $Repos.DjangoRw, '--db', $ix.Db, '--json')
    $c = @($arch.nodes | Where-Object { (Is-Container $_) -and $_.subKind -eq 'python-service' }) | Select-Object -First 1
    if ($c) {
        $http = Get-Entries $Repos.DjangoRw $ix.Db $c.key 'http' $null @()
        Check 10 'Django / DRF 入口' ($http.Count -ge 10) "http $($http.Count)，样例 $((@($http | Select-Object -First 3) | ForEach-Object { "$($_.httpMethod) $($_.route)" }) -join '；')，$($ix.Seconds)s"
        $e = $http | Select-Object -First 1
        if ($e) { $f = Get-FlowNodes $Repos.DjangoRw $ix.Db $e.symbolId @(); Check 10 'Django 视图流程' ($f.Nodes.Count -ge 3) "$($e.httpMethod) $($e.route)：$($f.Nodes.Count) 节点" }
    } else { Check 10 'Django / DRF 入口' $false '没认出 Python 容器' }
}
# ---------- 11. spring-petclinic（Java 单体）----------
if (Want 11) {
    $ix = Index-Repo 'petclinic' $Repos.PetClinic @()
    $arch = Get-Json @('arch', $Repos.PetClinic, '--db', $ix.Db, '--json')
    $c = @($arch.nodes | Where-Object { (Is-Container $_) -and $_.subKind -eq 'java-service' }) | Select-Object -First 1
    if ($c) {
        $http = Get-Entries $Repos.PetClinic $ix.Db $c.key 'http' $null @()
        Check 11 'Spring MVC 入口' ($http.Count -ge 15) "http $($http.Count)，样例 $((@($http | Select-Object -First 3) | ForEach-Object { "$($_.httpMethod) $($_.route)" }) -join '；')，$($ix.Seconds)s"
        $e = $http | Where-Object { $_.httpMethod -eq 'GET' -and $_.route -eq '/owners/{ownerId}' } | Select-Object -First 1
        if ($e) {
            $f = Get-FlowNodes $Repos.PetClinic $ix.Db $e.symbolId @()
            $repo = @($f.Nodes | Where-Object { $_.label -like '*findById*' }).Count -gt 0
            Check 11 '控制器 → Repository' $repo "GET /owners/{ownerId}：$($f.Nodes.Count) 节点，到 findById：$repo"
        } else { Check 11 '控制器 → Repository' $false '没找到 GET /owners/{ownerId}' }
    } else { Check 11 'Spring MVC 入口' $false '没认出 Java 容器' }
}

# ---------- 12. spring-petclinic-microservices（Java 微服务 + 网关）----------
if (Want 12) {
    $ix = Index-Repo 'petclinic-ms' $Repos.PetMicro @()
    $matches = Line-Value $ix.Log 'route-matches: (\d+)'
    $arch = Get-Json @('arch', $Repos.PetMicro, '--db', $ix.Db, '--json')
    $containers = @($arch.nodes | Where-Object { Is-Container $_ })
    Check 12 '微服务容器与跨服务匹配' (($containers.Count -ge 8) -and ([int]$matches -ge 10)) "容器 $($containers.Count)，route-matches $matches，$($ix.Seconds)s"
    $gw = $containers | Where-Object { $_.name -like '*api-gateway*' } | Select-Object -First 1
    $e = if ($gw) { Get-Entries $Repos.PetMicro $ix.Db $gw.key 'http' 'owners' @() | Where-Object { $_.route -like '*/owners/{ownerId}' } | Select-Object -First 1 }
    if ($e) {
        $f = Get-FlowNodes $Repos.PetMicro $ix.Db $e.symbolId @()
        $hops = @($f.Nodes | Where-Object { $_.kind -eq 'hop' })
        $cust = @($f.Nodes | Where-Object { $_.label -like '*OwnerResource*' }).Count -gt 0
        Check 12 '网关 → 客户服务续接' $cust "$($e.route)：$($f.Nodes.Count) 节点，hop $($hops.Count)，到 OwnerResource：$cust"
    } else { Check 12 '网关 → 客户服务续接' $false '没找到网关 /owners/{ownerId} 入口' }
}

# ---------- 13. CodeAtlas 自身（C# 控制台，无 Web 入口的那部分）----------
if (Want 13) {
    $ix = Index-Repo 'codeatlas' $Repos.CodeAtlas @()
    $main = Get-Entries $Repos.CodeAtlas $ix.Db 'src/CodeAtlas.Cli' 'hosted' $null @() | Select-Object -First 1
    if ($main) {
        $f = Get-FlowNodes $Repos.CodeAtlas $ix.Db $main.symbolId @()
        $cmd = @($f.Nodes | Where-Object { $_.label -like '*IndexCommand.Run*' }).Count -gt 0
        Check 13 '控制台 Main → 子命令' $cmd "$($main.name)：$($f.Nodes.Count) 节点，到 IndexCommand.Run：$cmd，$($ix.Seconds)s"
    } else { Check 13 '控制台 Main → 子命令' $false '没找到 src/CodeAtlas.Cli 的 Main 入口' }
}

# ---------- 14. Bing.NetCore（netcoreapp3.1、同目录多 csproj、引用未还原）----------
if (Want 14) {
    $ix = Index-Repo 'bing' $Repos.Bing @()
    $containers = Line-Value $ix.Log '^arch: containers: (\d+)'
    Check 14 '旧版 .NET 容器扫描不崩' ([int]$containers -ge 1) "容器 $containers，$($ix.Seconds)s"
    if (-not $NoRoslyn) {
        $kept = Line-Value $ix.Log 'kept: (\d+)'
        $errors = Line-Value $ix.Log 'errors: (\d+)'
        Check 14 'Roslyn 绑不上的调用保留启发式边' ([int]$kept -gt 0) "Roslyn 错误 $errors，保留 $kept 条"
    }
    # 包没还原时 Roslyn 认不出 ControllerBase：继承边要保留，控制器才认得出来（曾经 121 → 0）
    $http = Line-Value $ix.Log 'http: (\d+)'
    Check 14 '控制器入口不因 Roslyn 丢失' ([int]$http -ge 100) "http 入口 $http，保留继承 / 实现边 $(Line-Value $ix.Log 'kept-types: (\d+)') 条"
}
$results | Format-Table -AutoSize -Wrap
$failed = @($results | Where-Object { $_.结果 -ne '通过' }).Count
"共 $($results.Count) 项，不通过 $failed 项。日志与库在 $Work"
$results | ConvertTo-Json | Out-File (Join-Path $Work 'results.json') -Encoding utf8
exit $(if ($failed -eq 0) { 0 } else { 1 })
