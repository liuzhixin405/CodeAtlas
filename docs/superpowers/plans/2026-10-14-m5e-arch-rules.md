# M5e：NetArchTest 规则还原，叠加到 DSM

设计依据：`docs/superpowers/specs/2026-10-14-architecture-map-design.md` §3.2（声明规则违规的单元格标红边框）、§9 M5e。验收对象：ccs-backend 的 `CentralizedConfigurationSystem.Tests/Arch/PipelineArchitectureTests.cs`。

## 裁定
1. 规则只从测试项目（`ProjectInventory.IsTest`）的 .cs 文件里解析 `Types.…GetResult()` 链；跨行拼成一条语句；同文件 `const string` 先替换。
2. 规则三类：`forbid-dependency`（`ShouldNot().HaveDependencyOn[Any|All]` / `Should().NotHaveDependencyOn…`）、`require-dependency`（`Should().HaveDependencyOn`）、`placement`（`ImplementInterface(typeof(I)) … Should().ResideInNamespace…`，只展示不判违规）。其他谓词跳过并计数。
3. 匹配语义：组件键是命名空间前缀；规则 pattern 是 prefix / contains。组件比规则粗时不判（避免误报），钻到够细的层级才标。
4. 存 `arch_rules` 表（索引版本 7）；索引末尾的 `ArchitecturePostProcess` 里扫描并整表替换。
5. `architecture.yaml` 的 `rules:` 声明作为第二来源（Source = yaml），有余力再做。

### Task 1：解析与存储（Core）
- [x] `ArchRule` 模型、`ArchRuleParser.Parse(file, lines)`、`ArchRuleStorage.Replace/Load`。
- [x] 测试：ccs 的几种链写法（跨行、常量替换、HaveDependencyOnAny、NotHaveDependencyOn、placement、未知谓词计数）、写读。

### Task 2：叠加（Server）
- [x] `ArchitecturePostProcess.Run` 扫描规则并存库，打印 `rules: N`。
- [x] `DsmBuilder.Build(..., rules)`：`DsmDto.Rules` / `DsmDto.Violations(Row, Col, RuleId, Count)`；require 未满足 Count 0。
- [x] DsmTests：手造组件 + 规则钉 (Row, Col, RuleId)；粗层不判。

### Task 3：页面
- [x] `dsmView.ts`：违规格子红边框、require 未满足红虚框 `!`、格子详情列命中规则（测试名 + 文件:行）、表头统计 `声明规则 N 条，违规 M 处`。
- [x] `dsmView.test.ts` 断言。

### Task 4：验收
- [x] ccs-backend：解析出的规则条数、样例、Api 容器与 Application 层的违规格子。
- [x] `docs/acceptance.md` 5e 节；指南 §8.0.1 补一条。

## 实施记录（2026-10-14）
- ccs-backend 解析出 9 条规则（placement 5、forbid 4、跳过 0）。
- 违规：tree-sitter 索引上 1 处（Application → Infrastructure 20 条启发式边），Roslyn 索引上 0 处，与测试实际通过一致。
- architecture.yaml 的 rules: 声明未做（裁定 5 本来就是可选）。
