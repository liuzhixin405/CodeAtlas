# CodeAtlas 交付前回归：十七个"单独打开 / 合并打开"的真实项目场景（C# / TS / Go / Python / Java），逐项判定并打印结果表。
# 用法：powershell -File scripts\regression.ps1 [-Work <临时目录>] [-Only 1,5] [-NoRoslyn] [-Exe <codeatlas.exe>] [-Reuse]
# 依赖：dist\codeatlas.exe（先跑 scripts\publish.ps1；-Exe 可换成别的构建）；ccs-backend 与 eShop 的本机路径见下面的 $Repos。
# -Reuse：$Work\<场景>\index.db 与 index.log 已在时不重建索引，只重跑查询侧的检查（改了流程 / 大纲这类查询代码、索引没变时用）。
param(
    [string]$Work = (Join-Path $env:TEMP 'codeatlas-regression'),
    [int[]]$Only = @(),
    [switch]$NoRoslyn,
    [string]$Exe = '',
    [switch]$Reuse
)
$ErrorActionPreference = 'Continue'
# codeatlas 输出 UTF-8：控制台是 GBK（chcp 936）时 PowerShell 按 GBK 解码，中文会吞掉相邻的引号，--json 解析失败、流程全成 0 节点
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\dist\codeatlas.exe' }
$Exe = $Exe | Resolve-Path | Select-Object -ExpandProperty Path
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
    SpringRw   = 'D:\github\reference\spring-boot-realworld-example-app'
    ExpressRw  = 'D:\github\reference\node-express-realworld-example-app'
    NestRw     = 'D:\github\reference\nestjs-realworld-example-app'
}
New-Item -ItemType Directory -Force $Work | Out-Null
$results = New-Object System.Collections.Generic.List[object]

function Invoke-Ca([string[]]$CaArgs) {
    $out = & $Exe @CaArgs 2>&1 | ForEach-Object { "$_" }
    return ,$out
}

function Index-Repo([string]$Name, [string]$Root, [string[]]$Extra) {
    $db = Join-Path $Work "$Name\index.db"
    $logPath = Join-Path $Work "$Name\index.log"
    if ($Reuse -and (Test-Path $db) -and (Test-Path $logPath)) { return @{ Db = $db; Seconds = 0; Log = @(Get-Content $logPath -Encoding UTF8) } }
    $roslyn = if ($NoRoslyn) { 'off' } else { 'auto' }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = Invoke-Ca (@('index', $Root, '--db', $db, '--full', '--roslyn', $roslyn) + $Extra)
    $sw.Stop()
    $out | Out-File (Join-Path $Work "$Name\index.log") -Encoding utf8
    return @{ Db = $db; Seconds = [int]$sw.Elapsed.TotalSeconds; Log = $out }
}

function Get-Json([string[]]$CaArgs) {
    $text = (Invoke-Ca $CaArgs) -join "`n"
    # 前面可能有 "[roots] 主根 …" 这类以 [ 开头的提示行：逐个以 { / [ 开头的行试，直到能解析
    $starts = @(0) + @([regex]::Matches($text, "\n(?=[\[{])") | ForEach-Object { $_.Index + 1 })
    foreach ($st in $starts) {
        if ($st -ge $text.Length -or $text[$st] -notin @('[', '{')) { continue }
        try { return $text.Substring($st) | ConvertFrom-Json } catch { }
    }
    return $null
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

# 框架流程（arch --framework --json）：阶段键与全部节点（label / note / actor）
function Get-Framework($Root, $Db, $Key) {
    $fw = Get-Json @('arch', $Root, '--db', $Db, '--framework', $Key, '--json')
    $nodes = New-Object System.Collections.Generic.List[object]
    function WalkFw($n) { if ($null -eq $n) { return }; $nodes.Add($n); foreach ($c in @($n.children)) { WalkFw $c } }
    if ($fw) { WalkFw $fw.flow.root }
    return @{ Fw = $fw; Keys = @(if ($fw) { $fw.stages | ForEach-Object { $_.key } }); Nodes = $nodes; Labels = @($nodes | ForEach-Object { $_.label }) }
}

# 流程 / 框架流程的节点树摊平（label、kind、note、actor）
function Flatten-Flow($root) {
    $list = New-Object System.Collections.Generic.List[object]
    function WalkAll($n) { if ($null -eq $n) { return }; $list.Add($n); foreach ($c in @($n.children)) { WalkAll $c } }
    WalkAll $root
    return $list
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
    # 任意方法时序图（面板"调用图"的默认视图）：抽 30 个 HTTP 入口，以它分派到的 Handler 为根，前两层要看到调用大纲里的业务调用；
    # 再以 Handler 的第一个业务调用（Service / 仓储方法，不在入口表里）为根做一遍（算法见 src/CodeAtlas.Server/FlowCoverage.cs）
    $cov = Get-Json @('arch', $Repos.CcsBackend, '--db', $ix.Db, '--flow-coverage', 'CentralizedConfigurationSystem.Api', '--json')
    if ($cov) {
        Check 1 '任意方法时序图业务调用覆盖率（Handler 为根）' (($cov.sampled -ge 30) -and ($cov.ratio -ge 0.9)) ("抽样 $($cov.sampled)（跳过 $($cov.skipped)），前 2 层命中 $($cov.hits)，覆盖率 {0:P1}，平均召回 {1:P1}" -f $cov.ratio, $cov.recall)
        Check 1 '任意方法时序图业务调用覆盖率（下一层方法为根）' (($cov.nextSampled -ge 10) -and ($cov.nextRatio -ge 0.9)) ("$($cov.nextSampled) 个，命中 $($cov.nextHits)，覆盖率 {0:P1}" -f $cov.nextRatio)
    } else { Check 1 '任意方法时序图业务调用覆盖率（Handler 为根）' $false 'arch --flow-coverage 没有输出 JSON' }
    # 框架流程（项目地图「框架流程」）：认出自研分派器（不是 MediatR）与按 DI 注册排好的行为链
    $fw = Get-Json @('arch', $Repos.CcsBackend, '--db', $ix.Db, '--framework', 'CentralizedConfigurationSystem.Api', '--json')
    if ($fw) {
        $keys = @($fw.stages | ForEach-Object { $_.key })
        $beh = @($fw.flow.behaviors)
        $own = @($fw.notes | Where-Object { $_ -like '分派器：自研*' }).Count -eq 1
        $ordered = @($fw.stages | Where-Object { $_.key -eq 'behavior' -and $_.orderKnown }).Count -eq 1
        Check 1 '框架流程识别出分派器与行为链' (($keys -contains 'dispatcher') -and ($keys -contains 'behavior') -and ($beh.Count -ge 3) -and $own -and $ordered) ("阶段：" + ($keys -join ' → ') + "；行为 $($beh.Count) 个，顺序已知 $ordered；自研分派器 $own")
    } else { Check 1 '框架流程识别出分派器与行为链' $false 'arch --framework 没有输出 JSON' }
    # 请求流程里的管道：控制器 → 各行为（按注册顺序）→ Handler；验证器靠 AddValidatorsFromAssembly 扫描注册、源码里没有直接引用，按消息类型连上
    $exp = Get-Entries $Repos.CcsBackend $ix.Db 'CentralizedConfigurationSystem.Api' 'http' 'audit-logs/client-export' @() | Select-Object -First 1
    if ($exp) {
        $f = Get-FlowNodes $Repos.CcsBackend $ix.Db $exp.symbolId @()
        $top = @($f.Flow.root.children | ForEach-Object { $_.label })
        $vi = [array]::IndexOf($top, 'ValidationBehavior.HandleAsync'); $hi = [array]::IndexOf($top, 'LogClientExportHandler.HandleAsync')
        $validator = @($f.Nodes | Where-Object { $_.label -eq 'LogClientExportCommandValidator' }).Count -gt 0
        $skip = "$($f.Flow.note)" -like '*ApprovalBehavior*IApprovable*'
        Check 1 '请求流程带管道：验证 → Handler，验证器按消息类型连上' (($vi -ge 0) -and ($hi -gt $vi) -and $validator -and $skip) ("第一层：" + ($top -join ' → ') + "；验证器 $validator；跳过 Approval $skip")
    } else { Check 1 '请求流程带管道：验证 → Handler，验证器按消息类型连上' $false '没找到 audit-logs/client-export 入口' }

    # ---- 反例（2026-10-16 真实项目审计）：不该出现的东西 ----
    # 常驻服务 / Hub 按宿主真实注册归属：只在 Worker 跑的不得出现在 Api；Worker 没有 MapHub / AddKafkaConsumer
    $apiHosted = @(Get-Entries $Repos.CcsBackend $ix.Db 'CentralizedConfigurationSystem.Api' 'hosted' $null @() | ForEach-Object { $_.name })
    $workerHosted = @(Get-Entries $Repos.CcsBackend $ix.Db 'CentralizedConfigurationSystem.Worker' 'hosted' $null @() | ForEach-Object { $_.qualifiedName })
    $workerHubs = @(Get-Entries $Repos.CcsBackend $ix.Db 'CentralizedConfigurationSystem.Worker' 'hub' $null @())
    $apiWrong = @($apiHosted | Where-Object { $_ -match 'ApprovalExecutionBackgroundService|BackgroundJobWorkerHostedService|PluginInstallationSyncBackgroundService' })
    $workerWrong = @($workerHosted | Where-Object { $_ -match 'KafkaEventConsumerHostedService' })
    Check 1 '反例：常驻服务 / Hub 不串容器' (($apiWrong.Count -eq 0) -and ($workerWrong.Count -eq 0) -and ($workerHubs.Count -eq 0)) "Api 里的 Worker 服务 $($apiWrong.Count)；Worker 里的 Kafka 消费 $($workerWrong.Count)；Worker 的 Hub 入口 $($workerHubs.Count)"
    # 容器图：Kafka → Worker 的假边、共享契约 DLL 的假外部系统
    $carch = Get-Json @('arch', $Repos.CcsBackend, '--db', $ix.Db, '--json', '--no-store')
    $kafkaWorker = @($carch.edges | Where-Object { $_.toKey -eq 'CentralizedConfigurationSystem.Worker' -and $_.protocol -eq 'kafka-consume' })
    $contracts = @($carch.nodes | Where-Object { $_.name -like '*CcpSharedContracts*' })
    $keycloak = @($carch.nodes | Where-Object { $_.name -eq 'Keycloak' })
    Check 1 '反例：容器图没有假边与假外部系统' (($kafkaWorker.Count -eq 0) -and ($contracts.Count -eq 0) -and ($keycloak.Count -eq 1)) "Kafka→Worker $($kafkaWorker.Count)；CcpSharedContracts $($contracts.Count)；Keycloak 节点 $($keycloak.Count)"
    # 异步批量命令分派到 EnqueueJobHandler（只入队），不是后台作业 Handler
    $job = Get-Entries $Repos.CcsBackend $ix.Db 'CentralizedConfigurationSystem.Api' 'http' 'mt5/groups/job/patch-batch-async' @() | Select-Object -First 1
    if ($job) {
        $jf = Get-FlowNodes $Repos.CcsBackend $ix.Db $job.symbolId @()
        $enq = @($jf.Nodes | Where-Object { $_.label -like 'EnqueueJobHandler*' }).Count -gt 0
        $wrong = @($jf.Nodes | Where-Object { $_.label -like 'PatchBatchMt5GroupsJobHandler*' }).Count
        Check 1 '反例：异步命令不画成同步执行的作业' ($enq -and ($wrong -eq 0)) "EnqueueJobHandler $enq；PatchBatchMt5GroupsJobHandler $wrong"
    } else { Check 1 '反例：异步命令不画成同步执行的作业' $false '没找到 patch-batch-async 入口' }
    # 只读查询不得续接到 MT4Proxy 的批量写入（局部变量 url 曾被当成全文件字段）
    $sym = Get-Entries $Repos.CcsBackend $ix.Db 'CentralizedConfigurationSystem.Api' 'http' 'mt4/symbols' @() | Where-Object { $_.httpMethod -eq 'GET' -and $_.route -match '/api/v5/mt4/symbols$' } | Select-Object -First 1
    if ($sym) {
        $sf = Get-FlowNodes $Repos.CcsBackend $ix.Db $sym.symbolId @()
        $bulk = @($sf.Nodes | Where-Object { $_.label -like '*bulk-update*' -or $_.label -like '*CfgUpdateSymbol*' }).Count
        Check 1 '反例：列表查询不续接到批量写入' ($bulk -eq 0) "GET $($sym.route)：bulk-update / CfgUpdateSymbol 节点 $bulk"
    } else { Check 1 '反例：列表查询不续接到批量写入' $false '没找到 GET /api/v5/mt4/symbols' }
    # 不走分派器的入口不挂"管道行为"
    $roles = Get-Entries $Repos.CcsBackend $ix.Db 'CentralizedConfigurationSystem.Api' 'http' 'roles' @() | Where-Object { $_.httpMethod -eq 'GET' -and $_.route -match '/api/v5/roles$' } | Select-Object -First 1
    if ($roles) {
        $rf = Get-FlowNodes $Repos.CcsBackend $ix.Db $roles.symbolId @()
        Check 1 '反例：不经分派器的入口没有管道行为' (@($rf.Flow.behaviors).Count -eq 0) "GET /api/v5/roles 管道行为 $(@($rf.Flow.behaviors).Count) 个"
    } else { Check 1 '反例：不经分派器的入口没有管道行为' $false '没找到 GET /api/v5/roles' }
    # 框架流程：Handler 清单里没有后台作业 / 对比 Handler；Send 与 Query 合成一个分派器
    if ($fw) {
        $hItems = @($fw.handlers | ForEach-Object { $_.items } | ForEach-Object { $_.handler.qualifiedName })
        $badH = @($hItems | Where-Object { $_ -match 'JobHandler\b|CompareHandler\b' -and $_ -notmatch 'EnqueueJobHandler' })
        $fwAll = Flatten-Flow $fw.flow.root
        $merged = @($fwAll | Where-Object { $_.label -match 'QueryAsync / SendAsync|SendAsync / QueryAsync' }).Count -gt 0
        Check 1 '反例：框架流程 Handler 清单干净、分派器不分主次' (($badH.Count -eq 0) -and $merged) "Handler $($hItems.Count) 项，作业 / 对比 Handler $($badH.Count)；Send / Query 合并 $merged"
    }
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
    # 同一个 Handler 的时序图（query --flow）：接口调用与唯一实现合成一步（只画实现，note 写经接口），子节点按调用处的源码位置排
    $hf = Get-Json @('query', $Repos.EShop, "$handler(CreateOrderCommand, CancellationToken)", '--db', $ix.Db, '--flow', '--json')
    if (-not $hf -or -not $hf.root) { $hf = Get-Json @('query', $Repos.EShop, $handler, '--db', $ix.Db, '--flow', '--json') }
    if ($hf -and $hf.root) {
        $kids = @($hf.root.children)
        $iface = @($kids | Where-Object { $_.label -eq 'IOrderingIntegrationEventService.AddAndSaveEventAsync' }).Count
        $impl = @($kids | Where-Object { $_.label -eq 'OrderingIntegrationEventService.AddAndSaveEventAsync' -and $_.via -eq 'impl' -and $_.note -like '经接口 IOrderingIntegrationEventService*' }).Count
        $lines = @($kids | Where-Object { $_.kind -in @('call', 'dispatch', 'hop', 'http') -and $null -ne $_.line } | ForEach-Object { [int]$_.line })
        $sorted = ($lines -join ',') -eq ((@($lines | Sort-Object)) -join ',')
        Check 4 '时序图：接口与唯一实现合成一步、按源码位置排' (($iface -eq 0) -and ($impl -eq 1) -and $sorted -and ($lines.Count -ge 3)) ("子节点：" + (($kids | ForEach-Object { "$($_.label)@$($_.line)" }) -join ' → ') + "；接口节点 $iface，经接口的实现 $impl")
    } else { Check 4 '时序图：接口与唯一实现合成一步、按源码位置排' $false 'query --flow 没有输出 JSON' }
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
    # 框架流程：Logging / Validator / Transaction 三个 MediatR 行为按 AddOpenBehavior 的书写顺序，TransactionBehavior 在 next() 之后的提交落在收尾列
    $fw = Get-Json @('arch', $Repos.EShop, '--db', $ix.Db, '--framework', 'src/Ordering.API', '--json')
    if ($fw) {
        $beh = @($fw.flow.behaviors)
        $fwNodes = New-Object System.Collections.Generic.List[object]
        function Walk-Framework($n) { if ($null -eq $n) { return }; $fwNodes.Add($n); foreach ($c in @($n.children)) { Walk-Framework $c } }
        Walk-Framework $fw.flow.root
        $commit = @($fwNodes | Where-Object { $_.actor -eq 'tail' -and $_.label -like '*CommitTransactionAsync*' -and $_.note -like 'TransactionBehavior 后置*' }).Count
        Check 4 '框架流程三个行为按注册顺序、收尾有提交' ((($beh -join ',') -eq 'LoggingBehavior,ValidatorBehavior,TransactionBehavior') -and ($commit -ge 1)) ("行为：" + ($beh -join ' → ') + "；收尾里的提交 $commit；阶段：" + ((@($fw.stages) | ForEach-Object { $_.key }) -join ' → '))
    } else { Check 4 '框架流程三个行为按注册顺序、收尾有提交' $false 'arch --framework 没有输出 JSON' }

    # ---- 反例（2026-10-16 真实项目审计）----
    $earch = Get-Json @('arch', $Repos.EShop, '--db', $ix.Db, '--json', '--no-store')
    $producers = @($earch.edges | Where-Object { $_.protocol -eq 'amqp-produce' } | ForEach-Object { $_.fromKey } | Sort-Object -Unique)
    $extraProd = @($producers | Where-Object { $_ -notin @('src/Catalog.API', 'src/Ordering.API', 'src/OrderProcessor', 'src/PaymentProcessor') })
    Check 4 '反例：只有真正发消息的容器有 RabbitMQ 发布边' (($extraProd.Count -eq 0) -and ($producers.Count -ge 3)) ("发布方：" + ($producers -join '、'))
    $roots = @()
    foreach ($c in @($earch.nodes | Where-Object { Is-Container $_ })) { if (@(Get-Entries $Repos.EShop $ix.Db $c.key 'http' $null @() | Where-Object { $_.route -eq '/' -and $_.file -like '*ServiceDefaults*' }).Count -gt 0) { $roots += $c.name } }
    Check 4 '反例：共享库的 OpenAPI 路由只算调用它的容器' (@($roots | Where-Object { $_ -notin @('Catalog.API', 'Ordering.API', 'Webhooks.API') }).Count -eq 0) ("有 ServiceDefaults 的 GET /：" + ($roots -join '、'))
    $idRoot = @(Get-Entries $Repos.EShop $ix.Db 'src/Identity.API' 'http' $null @() | Where-Object { $_.route -eq '/' -and $_.file -like '*Controller*' })
    Check 4 '反例：MVC 约定路由不显示成 /' ($idRoot.Count -eq 0) "Identity.API 控制器路由为 / 的 $($idRoot.Count) 个"
    $orders = Get-Entries $Repos.EShop $ix.Db 'src/Ordering.API' 'http' $null @() | Where-Object { $_.httpMethod -eq 'POST' -and $_.route -eq '/api/orders' } | Select-Object -First 1
    if ($orders) {
        $of = Get-FlowNodes $Repos.EShop $ix.Db $orders.symbolId @()
        $post = @($of.Nodes | Where-Object { $_.label -like '*CommitTransactionAsync*' -and "$($_.note)" -like '*TransactionBehavior 后置*' }).Count
        Check 4 '请求流程里有行为 next() 之后的提交' ($post -ge 1) "POST /api/orders：TransactionBehavior 后置的提交 $post"
    } else { Check 4 '请求流程里有行为 next() 之后的提交' $false '没找到 POST /api/orders' }
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
    # ---- 反例（2026-10-16）：外部系统命名、前端框架流程的代理一跳 ----
    $uarch = Get-Json @('arch', $Repos.CcsUi, '--db', $ix.Db, '--json', '--no-store')
    $badExt = @($uarch.nodes | Where-Object { $_.name -in @('API /negotiate', 'API /realms') })
    $kc = @($uarch.nodes | Where-Object { $_.name -eq 'Keycloak' })
    Check 5 '反例：外部系统按服务命名' (($badExt.Count -eq 0) -and ($kc.Count -eq 1) -and ([int]$outbound -ge 420)) "出口 $outbound；API /negotiate、API /realms $($badExt.Count)；Keycloak $($kc.Count)"
    $ufw = Get-Framework $Repos.CcsUi $ix.Db 'ccs-src/ccs-app'
    $proxy = @($ufw.Nodes | Where-Object { $_.label -like '代理 /api/proxy-gateway*' }).Count -gt 0
    $wrongNote = @($ufw.Fw.notes | Where-Object { $_ -like '*不在页面的请求路径上*' }).Count
    Check 5 '前端框架流程画出代理路由' ($proxy -and ($wrongNote -eq 0)) "代理一跳 $proxy；错误说明 $wrongNote"
}

# ---------- 6. ccs-ui + 外部根 ccs-backend ----------
if (Want 6) {
    $extra = @('--project', $Repos.CcsBackend)
    $ix = Index-Repo 'ccs-ui+backend' $Repos.CcsUi $extra
    $out = Invoke-Ca (@('arch', $Repos.CcsUi, '--db', $ix.Db) + $extra)
    $containers = Line-Value $out '^容器 (\d+)'
    Check 6 '前后端五个容器' ([int]$containers -ge 5) "容器 $containers，$($ix.Seconds)s"
    Check-GroupsChain 6 $Repos.CcsUi $ix.Db 'ccs-src/ccs-app' $extra
    # ---- 反例（2026-10-16）：合并后端时不再有和 Api 重复的外部节点，Keycloak 只有一个 ----
    # 存库模式：去掉与容器重复的外部节点是在入口 / 路由匹配那一步（--no-store 不跑）
    $march = Get-Json (@('arch', $Repos.CcsUi, '--db', $ix.Db, '--json') + $extra)
    $dupApi = @($march.nodes | Where-Object { $_.name -eq 'API /api/v5' })
    $kcs = @($march.nodes | Where-Object { $_.name -eq 'Keycloak' })
    Check 6 '反例：合并模式没有重复的外部节点' (($dupApi.Count -eq 0) -and ($kcs.Count -eq 1)) "API /api/v5 $($dupApi.Count)；Keycloak $($kcs.Count)"
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
            # 框架流程（Go）：手工包装的 handler 链按执行顺序（最后包的最先执行）
            $g = Get-Framework $Repos.Boutique $ix.Db $fe.key
            $oi = [array]::IndexOf($g.Labels, 'otelhttp.NewHandler'); $ei = [array]::IndexOf($g.Labels, 'ensureSessionID'); $li = [array]::IndexOf($g.Labels, 'logHandler.ServeHTTP')
            Check 7 'Go 框架流程：包装链按执行顺序' (($g.Keys -contains 'middleware') -and ($oi -ge 0) -and ($ei -gt $oi) -and ($li -gt $ei)) ("阶段：" + ($g.Keys -join ' → ') + "；otelhttp $oi → ensureSessionID $ei → logHandler $li")
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
        $g = Get-Framework $Repos.FastApi $ix.Db $be.key
        Check 8 'FastAPI 框架流程：中间件与依赖链' (($g.Keys -contains 'dependency') -and ($g.Labels -contains 'CORSMiddleware') -and ($g.Labels -contains 'get_current_user')) ("阶段：" + ($g.Keys -join ' → ') + "；CORSMiddleware $($g.Labels -contains 'CORSMiddleware')；get_current_user $($g.Labels -contains 'get_current_user')")
    } else { Check 8 'FastAPI 入口带前缀' $false '没认出 backend 容器' }
    $fe = @($arch.nodes | Where-Object { (Is-Container $_) -and $_.subKind -eq 'frontend' }) | Select-Object -First 1
    if ($fe) {
        # TanStack Router 文件路由登记成页面入口（__root 与 _layout 布局不算）
        $pages = @(Get-Entries $Repos.FastApi $ix.Db $fe.key 'page' $null @())
        $routes = @($pages | ForEach-Object { $_.route } | Sort-Object)
        Check 8 'React SPA 路由登记成页面入口' (($pages.Count -ge 8) -and ($routes -contains '/') -and ($routes -contains '/items') -and ($routes -contains '/login')) ("页面 $($pages.Count)：" + ($routes -join '、'))
        $g = Get-Framework $Repos.FastApi $ix.Db $fe.key
        $guard = @($g.Nodes | Where-Object { "$($_.note)" -like '*isLoggedIn*' }).Count -gt 0
        Check 8 '前端框架流程：Provider → 路由（守卫）→ 页面' (($g.Keys -contains 'provider') -and ($g.Keys -contains 'route') -and ($g.Labels -contains 'QueryClientProvider') -and $guard) ("阶段：" + ($g.Keys -join ' → ') + "；登录守卫 $guard")
    } else { Check 8 'React SPA 路由登记成页面入口' $false '没认出 frontend 容器' }
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
        $g = Get-Framework $Repos.GinRw $ix.Db $c.key
        $auth = @($g.Labels | Where-Object { $_ -like 'users.AuthMiddleware*' }).Count
        Check 9 'gin 框架流程：默认中间件与组中间件' (($g.Labels -contains 'gin.Logger()') -and ($auth -ge 2)) ("阶段：" + ($g.Keys -join ' → ') + "；AuthMiddleware $auth 处")
        # 反例（2026-10-16）：AuthMiddleware(true) 之后注册的 ArticlesRegister 要经过鉴权，说明不能说反
        $authNote = "$(@($g.Nodes | Where-Object { $_.label -eq 'users.AuthMiddleware(true)' } | Select-Object -First 1).note)"
        Check 9 '反例：组中间件说明按注册顺序' (($authNote -like '*之后的*ArticlesRegister*') -or ($authNote -like '*之后*ArticlesRegister*')) ($authNote.Substring(0, [Math]::Min(120, $authNote.Length)))
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
        # /admin/ 是 admin.site.urls（2026-10-16 起认出），落在 urls.py 模块上，不是视图
        $e = $http | Where-Object { $_.route -ne '/admin/' } | Select-Object -First 1
        if ($e) { $f = Get-FlowNodes $Repos.DjangoRw $ix.Db $e.symbolId @(); Check 10 'Django 视图流程' ($f.Nodes.Count -ge 3) "$($e.httpMethod) $($e.route)：$($f.Nodes.Count) 节点" }
        $g = Get-Framework $Repos.DjangoRw $ix.Db $c.key
        $mw = @($g.Nodes | Where-Object { $_.actor -eq 'middleware' -and $_.label -like '*Middleware' }).Count
        $handler = @($g.Labels | Where-Object { $_ -like '*core_exception_handler*' }).Count -gt 0
        Check 10 'Django 框架流程：MIDDLEWARE 顺序与 DRF 认证、异常处理' (($mw -ge 8) -and ($g.Labels -contains 'JWTAuthentication.authenticate') -and $handler) ("阶段：" + ($g.Keys -join ' → ') + "；中间件 $mw；异常处理 $handler")
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
        $g = Get-Framework $Repos.PetClinic $ix.Db $c.key
        Check 11 'Spring 框架流程：拦截器' (($g.Keys -contains 'interceptor') -and ($g.Labels -contains 'LocaleChangeInterceptor')) ("阶段：" + ($g.Keys -join ' → '))
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
    if ($gw) {
        $g = Get-Framework $Repos.PetMicro $ix.Db $gw.key
        Check 12 'Gateway 框架流程：过滤器链' (($g.Labels -contains 'CircuitBreaker') -and ($g.Labels -contains 'Retry')) ("阶段：" + ($g.Keys -join ' → ') + "；" + (($g.Labels | Select-Object -Skip 1 -First 5) -join ' → '))
    }
}

# ---------- 13. CodeAtlas 自身（C# 控制台，无 Web 入口的那部分）----------
if (Want 13) {
    $ix = Index-Repo 'codeatlas' $Repos.CodeAtlas @()
    # 2026-10-16 起不起宿主的控制台 Main 是 command（不是常驻服务）
    $main = Get-Entries $Repos.CodeAtlas $ix.Db 'src/CodeAtlas.Cli' 'command' $null @() | Where-Object { $_.name -eq 'Main' } | Select-Object -First 1
    if ($main) {
        $f = Get-FlowNodes $Repos.CodeAtlas $ix.Db $main.symbolId @()
        $cmd = @($f.Nodes | Where-Object { $_.label -like '*IndexCommand.Run*' }).Count -gt 0
        Check 13 '控制台 Main → 子命令' $cmd "$($main.name)：$($f.Nodes.Count) 节点，到 IndexCommand.Run：$cmd，$($ix.Seconds)s"
    } else { Check 13 '控制台 Main → 子命令' $false '没找到 src/CodeAtlas.Cli 的 Main 入口' }
    # ---- 反例（2026-10-16 审计：在 CodeAtlas 自己身上看到的问题）----
    $http = @(Get-Entries $Repos.CodeAtlas $ix.Db 'src/CodeAtlas.Cli' 'http' $null @())
    $distinct = @($http | ForEach-Object { $_.symbolId } | Sort-Object -Unique).Count
    Check 13 '反例：lambda 路由不挂在同一个方法上' (($http.Count -ge 20) -and ($distinct -ge $http.Count - 2)) "HTTP 入口 $($http.Count)，不同符号 $distinct"
    $sarch = Get-Json @('arch', $Repos.CodeAtlas, '--db', $ix.Db, '--json', '--no-store')
    $sc = @($sarch.nodes | Where-Object { Is-Container $_ })
    $fixture = @($sc | Where-Object { $_.path -like 'tests/*' })
    $ext = @($sc | Where-Object { $_.subKind -eq 'vscode-extension' })
    Check 13 '反例：测试夹具不是容器，扩展是容器' (($fixture.Count -eq 0) -and ($ext.Count -eq 1)) ("容器：" + (($sc | ForEach-Object { "$($_.name)[$($_.subKind)]" }) -join '、'))
    $dsm = Get-Json @('arch', $Repos.CodeAtlas, '--db', $ix.Db, '--dsm', 'src/CodeAtlas.Cli', '--json')
    Check 13 '反例：测试字符串里的规则不算声明规则' (@($dsm.rules).Count -eq 0) "声明规则 $(@($dsm.rules).Count) 条"
    $cfw = Get-Framework $Repos.CodeAtlas $ix.Db 'src/CodeAtlas.Cli'
    $notWeb = @($cfw.Fw.notes | Where-Object { $_ -like '*不是 Web 服务*' }).Count
    Check 13 '反例：跑着 Kestrel 的 CLI 按 Web 画' (($notWeb -eq 0) -and ($cfw.Keys -contains 'middleware')) ("阶段：" + ($cfw.Keys -join ' → ') + "；说『不是 Web』$notWeb")
    $run = Get-Json @('query', $Repos.CodeAtlas, 'IndexCommand.Run', '--db', $ix.Db, '--flow', '--depth', '1', '--json')
    $first = @(@($run)[0].root.children | ForEach-Object { $_.label })
    $key = @($first | Where-Object { $_ -match 'IndexAsync|IndexDatabase.Open|ArchitecturePostProcess.Run' }).Count
    Check 13 '时序图第一层有真正干活的调用' ($key -ge 2) ("IndexCommand.Run 第一层：" + ($first -join '、'))
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
# ---------- 15. Spring Boot RealWorld（Security 过滤器链、JWT 过滤器、全局异常处理）----------
if (Want 15) {
    $ix = Index-Repo 'spring-rw' $Repos.SpringRw @()
    $arch = Get-Json @('arch', $Repos.SpringRw, '--db', $ix.Db, '--json')
    $c = @($arch.nodes | Where-Object { (Is-Container $_) -and $_.subKind -eq 'java-service' }) | Select-Object -First 1
    if ($c) {
        $http = Get-Entries $Repos.SpringRw $ix.Db $c.key 'http' $null @()
        $g = Get-Framework $Repos.SpringRw $ix.Db $c.key
        $advice = @($g.Labels | Where-Object { $_ -like '*CustomizeExceptionHandler*' }).Count
        Check 15 'Spring 框架流程：Security 链里的 JWT 过滤器、异常处理' (($http.Count -ge 15) -and ($g.Labels -contains 'JwtTokenFilter.doFilterInternal') -and ($advice -ge 1)) ("http $($http.Count)；阶段：" + ($g.Keys -join ' → ') + "；异常处理 $advice 行，$($ix.Seconds)s")
    } else { Check 15 'Spring 框架流程：Security 链里的 JWT 过滤器、异常处理' $false '没认出 Java 容器' }
}

# ---------- 16. Express RealWorld（TS）----------
if (Want 16) {
    $ix = Index-Repo 'express-rw' $Repos.ExpressRw @()
    $arch = Get-Json @('arch', $Repos.ExpressRw, '--db', $ix.Db, '--json')
    $c = @($arch.nodes | Where-Object { (Is-Container $_) -and $_.subKind -eq 'node-service' }) | Select-Object -First 1
    if ($c) {
        $g = Get-Framework $Repos.ExpressRw $ix.Db $c.key
        $mounts = @($g.Labels | Where-Object { $_ -like '挂载 /api*' }).Count
        $err = @($g.Nodes | Where-Object { "$($_.note)" -like '*出错时*' }).Count -gt 0
        Check 16 'Express 框架流程：app.use 顺序、路由挂载、错误中间件' (($g.Labels -contains 'cors') -and ($mounts -ge 3) -and $err) ("阶段：" + ($g.Keys -join ' → ') + "；/api 挂载 $mounts；出错时 $err，$($ix.Seconds)s")
        # 反例（2026-10-16）：多行路由不漏、挂载前缀拼上、内联处理器不并成一个文件符号
        $eh = @(Get-Entries $Repos.ExpressRw $ix.Db $c.key 'http' $null @())
        $noPrefix = @($eh | Where-Object { $_.route -ne '/' -and $_.route -notlike '/api/*' })
        $dupSym = @($eh | Group-Object symbolId | Where-Object { $_.Count -gt 1 -and @($_.Group | ForEach-Object { $_.route } | Sort-Object -Unique).Count -gt 1 })
        Check 16 '反例：Express 路由齐全、带前缀、不共用文件符号' (($eh.Count -ge 18) -and ($noPrefix.Count -eq 0) -and ($dupSym.Count -eq 0)) "http $($eh.Count)；缺 /api 前缀 $($noPrefix.Count)；不同路由共用一个符号 $($dupSym.Count) 组"
    } else { Check 16 'Express 框架流程：app.use 顺序、路由挂载、错误中间件' $false '没认出 Node 容器' }
}

# ---------- 17. NestJS RealWorld（没有 Dockerfile，靠 start 脚本认成服务）----------
if (Want 17) {
    $ix = Index-Repo 'nest-rw' $Repos.NestRw @()
    $arch = Get-Json @('arch', $Repos.NestRw, '--db', $ix.Db, '--json')
    $c = @($arch.nodes | Where-Object { (Is-Container $_) -and $_.subKind -eq 'node-service' }) | Select-Object -First 1
    if ($c) {
        $http = Get-Entries $Repos.NestRw $ix.Db $c.key 'http' $null @()
        $g = Get-Framework $Repos.NestRw $ix.Db $c.key
        $auth = @($g.Labels | Where-Object { $_ -eq 'AuthMiddleware.use' }).Count
        Check 17 'NestJS 容器、入口与模块中间件' (($http.Count -ge 15) -and ($auth -ge 2)) ("http $($http.Count)；阶段：" + ($g.Keys -join ' → ') + "；AuthMiddleware $auth 个模块，$($ix.Seconds)s")
    } else { Check 17 'NestJS 容器、入口与模块中间件' $false '没认出 Node 容器（没有 Dockerfile 时要靠 start 脚本）' }
}

$results | Format-Table -AutoSize -Wrap
$failed = @($results | Where-Object { $_.结果 -ne '通过' }).Count
"共 $($results.Count) 项，不通过 $failed 项。日志与库在 $Work"
$results | ConvertTo-Json | Out-File (Join-Path $Work 'results.json') -Encoding utf8
exit $(if ($failed -eq 0) { 0 } else { 1 })
