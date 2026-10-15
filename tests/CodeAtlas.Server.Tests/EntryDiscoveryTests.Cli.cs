using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 命令行框架的命令入口（kind = command，route = 子命令路径，空格连接；根 / 默认命令用容器名）：
/// C# System.CommandLine / Spectre.Console.Cli / CommandLineParser，Go cobra / urfave-cli，Python click / typer / argparse，Java picocli，Node commander / yargs。
/// 入口的符号是处理函数（流程从它往下走），File / Line 是处理函数的位置。
/// </summary>
public partial class EntryDiscoveryTests
{
    private static readonly Dictionary<string, string[]> CliFiles = new()
    {
        // ── C#：System.CommandLine ──
        ["Tool/Program.cs"] =
        [
            "using System.CommandLine;",                                               // 1
            "namespace Tool;",                                                         // 2
            "public static class Program",                                             // 3
            "{",                                                                       // 4
            "    public static int Main(string[] args)",                               // 5
            "    {",                                                                   // 6
            "        var root = new RootCommand(\"demo tool\");",                      // 7：RootCommand 的字符串是描述
            "        var index = new Command(\"index\", \"Index a repo\");",           // 8
            "        index.SetHandler(IndexHandler.Run);",                             // 9：方法组
            "        var remote = new Command(\"remote\");",                           // 10：没有处理器 → 不是入口
            "        var add = new Command(\"add\") { };",                             // 11
            "        add.SetHandler(async (string url) => await RemoteOps.AddAsync(url));", // 12：lambda → 第一个能找到的调用
            "        remote.AddCommand(add);",                                         // 13：父子 → remote add
            "        root.AddCommand(index);",                                         // 14
            "        root.AddCommand(remote);",                                        // 15
            "        var serve = new Command(\"serve\");",                             // 16
            "        serve.Handler = CommandHandler.Create(ServeHandler.Process);",    // 17：beta 版的 Handler 赋值
            "        root.Subcommands.Add(serve);",                                    // 18
            "        // var gone = new Command(\"gone\"); gone.SetHandler(Program.Main);", // 19：注释
            "        return root.Invoke(args);",                                       // 20
            "    }",                                                                   // 21
            "}",                                                                       // 22
        ],
        ["Tool/Handlers.cs"] =
        [
            "namespace Tool;",                                                                      // 1
            "public static class IndexHandler { public static int Run(string path) => 0; }",       // 2
            "public static class RemoteOps { public static Task AddAsync(string url) => Task.CompletedTask; }", // 3
            "public static class ServeHandler { public static int Process(int port) => 0; }",      // 4
            "public sealed class StatsCommand : Command",                                           // 5：子类形式
            "{",                                                                                    // 6
            "    public StatsCommand() : base(\"stats\", \"Show stats\")",                          // 7
            "    {",                                                                                // 8
            "        this.SetHandler(Execute);",                                                    // 9
            "    }",                                                                                // 10
            "    private void Execute() { }",                                                       // 11
            "}",                                                                                    // 12
        ],
        // ── C#：Spectre.Console.Cli ──
        ["Shell/Program.cs"] =
        [
            "using Spectre.Console.Cli;",                                    // 1
            "var app = new CommandApp<DefaultCommand>();",                   // 2：默认命令 → route = 容器名
            "app.Configure(config =>",                                       // 3
            "{",                                                             // 4
            "    config.AddCommand<AddCommand>(\"add\");",                   // 5
            "    config.AddBranch(\"remote\", remote =>",                    // 6：lambda 体从下一行的 { 开始
            "    {",                                                         // 7
            "        remote.AddCommand<RemoteAddCommand>(\"add\");",         // 8：remote add
            "    });",                                                       // 9
            "    config.AddBranch(\"db\", db => db.AddCommand<MigrateCommand>(\"migrate\"));", // 10：单行 lambda
            "    config.AddCommand<AddCommand>(\"plus\");",                  // 11：分支结束后不再带前缀
            "});",                                                           // 12
            "return app.Run(args);",                                         // 13
        ],
        ["Shell/Commands.cs"] =
        [
            "using Spectre.Console.Cli;",                                                                       // 1
            "public sealed class DefaultCommand : Command<DefaultCommand.Settings>",                           // 2
            "{",                                                                                                // 3
            "    public override int Execute(CommandContext context, Settings settings) => 0;",                // 4
            "}",                                                                                                // 5
            "public sealed class AddCommand : AsyncCommand<AddCommand.Settings>",                              // 6
            "{",                                                                                                // 7
            "    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => Task.FromResult(0);", // 8
            "}",                                                                                                // 9
            "public sealed class RemoteAddCommand : Command<RemoteAddCommand.Settings>",                       // 10
            "{",                                                                                                // 11
            "    public override int Execute(CommandContext context, Settings settings) => 0;",                // 12
            "}",                                                                                                // 13
            "public sealed class MigrateCommand : Command<MigrateCommand.Settings>",                           // 14
            "{",                                                                                                // 15
            "    public override int Execute(CommandContext context, Settings settings) => 0;",                // 16
            "}",                                                                                                // 17
            "public sealed class ListPackagesCommand : Command<ListPackagesCommand.Settings>",                 // 18：没注册 → 按类名推 list-packages
            "{",                                                                                                // 19
            "    public override int Execute(CommandContext context, Settings settings) => 0;",                // 20
            "}",                                                                                                // 21
        ],
        // ── C#：CommandLineParser ──
        ["Clp/Program.cs"] =
        [
            "using CommandLine;",                                                         // 1
            "[Verb(\"add\", HelpText = \"Add file contents to the index.\")]",            // 2
            "class AddOptions { }",                                                       // 3
            "[Verb(\"commit\", HelpText = \"Record changes.\")]",                         // 4
            "class CommitOptions { }",                                                    // 5
            "[Verb(\"clone\")]",                                                          // 6
            "class CloneOptions { }",                                                     // 7
            "static class Program",                                                       // 8
            "{",                                                                          // 9
            "    static int Main(string[] args) =>",                                      // 10
            "        Parser.Default.ParseArguments<AddOptions, CommitOptions, CloneOptions>(args)", // 11
            "            .MapResult(",                                                    // 12
            "                (AddOptions opts) => RunAdd(opts),",                         // 13
            "                (CommitOptions opts) => Git.Commit(opts),",                  // 14
            "                errs => 1);",                                                // 15
            "    static int RunAdd(AddOptions o) => 0;",                                  // 16
            "}",                                                                          // 17
        ],
        ["Clp/Git.cs"] =
        [
            "static class Git",                                            // 1
            "{",                                                           // 2
            "    public static int Commit(CommitOptions o) => 0;",         // 3
            "    public static int Clone(CloneOptions o) => 0;",           // 4：MapResult 里没写 → 参数类型是 CloneOptions 的方法
            "}",                                                           // 5
        ],
        // ── Go：cobra ──
        ["gocli/cmd/root.go"] =
        [
            "package cmd",                                   // 1
            "",                                              // 2
            "var rootCmd = &cobra.Command{",                 // 3
            "\tUse:   \"tool\",",                            // 4：根命令没有 Run → 不是入口；名字不进子命令路径
            "\tShort: \"demo\",",                            // 5
            "}",                                             // 6
            "",                                              // 7
            "var serveCmd = &cobra.Command{",                // 8
            "\tUse:  \"serve [port]\",",                     // 9
            "\tRunE: runServe,",                             // 10
            "}",                                             // 11
            "",                                              // 12
            "func init() {",                                 // 13
            "\trootCmd.AddCommand(serveCmd, newRemoteCmd())", // 14
            "}",                                             // 15
            "",                                              // 16
            "func runServe(cmd *cobra.Command, args []string) error { return nil }", // 17
        ],
        ["gocli/cmd/remote.go"] =
        [
            "package cmd",                                          // 1
            "",                                                     // 2
            "func newRemoteCmd() *cobra.Command {",                 // 3：构造函数里 new 再 return
            "\tcmd := &cobra.Command{Use: \"remote\"}",             // 4
            "\tadd := &cobra.Command{",                             // 5
            "\t\tUse: \"add <name> <url>\",",                       // 6
            "\t\tRun: func(c *cobra.Command, args []string) {",     // 7：func 字面量 → 里面第一个能找到的调用
            "\t\t\taddRemote(args[0], args[1])",                    // 8
            "\t\t},",                                               // 9
            "\t}",                                                  // 10
            "\tcmd.AddCommand(add)",                                // 11
            "\treturn cmd",                                         // 12
            "}",                                                    // 13
            "",                                                     // 14
            "func addRemote(name, url string) {}",                  // 15
        ],
        // ── Go：urfave/cli ──
        ["urf/main.go"] =
        [
            "package main",                                   // 1
            "",                                               // 2
            "import \"github.com/urfave/cli/v2\"",            // 3
            "",                                               // 4
            "func main() {",                                  // 5
            "\tapp := &cli.App{",                             // 6
            "\t\tName: \"greet\",",                           // 7
            "\t\tAction: func(c *cli.Context) error {",       // 8：根命令的 Action → route = 它的 Name
            "\t\t\treturn greet(c)",                          // 9
            "\t\t},",                                         // 10
            "\t\tCommands: []*cli.Command{",                  // 11
            "\t\t\t{",                                        // 12
            "\t\t\t\tName:   \"serve\",",                     // 13
            "\t\t\t\tAction: serve,",                         // 14
            "\t\t\t},",                                       // 15
            "\t\t\t{",                                        // 16
            "\t\t\t\tName: \"db\",",                          // 17
            "\t\t\t\tSubcommands: []*cli.Command{",           // 18
            "\t\t\t\t\t{Name: \"migrate\", Action: migrate},", // 19：db migrate
            "\t\t\t\t},",                                     // 20
            "\t\t\t},",                                       // 21
            "\t\t},",                                         // 22
            "\t}",                                            // 23
            "\tapp.Run(os.Args)",                             // 24
            "}",                                              // 25
            "func greet(c *cli.Context) error { return nil }",   // 26
            "func serve(c *cli.Context) error { return nil }",   // 27
            "func migrate(c *cli.Context) error { return nil }", // 28
        ],
        // ── Python：click ──
        ["pycli/cli.py"] =
        [
            "import click",               // 1
            "",                           // 2
            "@click.group()",             // 3：根 group，名字不进路径，本身不是入口
            "def cli():",                 // 4
            "    pass",                   // 5
            "",                           // 6
            "@cli.command()",             // 7
            "def init_db():",             // 8：函数名 → init-db
            "    setup()",                // 9
            "",                           // 10
            "@cli.group()",               // 11
            "def remote():",              // 12
            "    pass",                   // 13
            "",                           // 14
            "@remote.command(\"add\")",   // 15
            "@click.argument(\"url\")",   // 16
            "def remote_add(url):",       // 17：remote add
            "    pass",                   // 18
            "",                           // 19
            "@click.command(name=\"sync\")", // 20
            "def do_sync():",             // 21
            "    pass",                   // 22
            "",                           // 23
            "cli.add_command(do_sync)",   // 24：注册到根 group
            "",                           // 25
            "@click.command()",           // 26
            "def build_command():",       // 27：click 8.2 去掉 _command 后缀 → build
            "    pass",                   // 28
            "",                           // 29
            "cli.add_command(build_command)", // 30
        ],
        // ── Python：typer ──
        ["pycli/app.py"] =
        [
            "import typer",                          // 1
            "from . import users",                   // 2
            "",                                      // 3
            "app = typer.Typer()",                   // 4
            "app.add_typer(users.app, name=\"users\")", // 5：子应用前缀
            "",                                      // 6
            "@app.command()",                        // 7
            "def hello(name: str):",                 // 8
            "    greet(name)",                       // 9
            "",                                      // 10
            "@app.command(\"bye\")",                 // 11
            "def goodbye():",                        // 12
            "    pass",                              // 13
        ],
        ["pycli/users.py"] =
        [
            "import typer",                  // 1
            "",                              // 2
            "app = typer.Typer()",           // 3
            "",                              // 4
            "@app.command()",                // 5
            "def create_user(name: str):",   // 6：users create-user
            "    pass",                      // 7
        ],
        // ── Python：argparse ──
        ["pycli/tool.py"] =
        [
            "import argparse",                                          // 1
            "",                                                         // 2
            "def main():",                                              // 3
            "    parser = argparse.ArgumentParser()",                   // 4
            "    sub = parser.add_subparsers(dest=\"cmd\")",            // 5
            "    p = sub.add_parser(\"serve\")",                        // 6
            "    p.set_defaults(func=serve)",                           // 7
            "    p = sub.add_parser(\"migrate\", help=\"db\")",         // 8：同一个变量名复用
            "    p.set_defaults(func=migrate)",                         // 9
            "    remote = sub.add_parser(\"remote\")",                  // 10
            "    rsub = remote.add_subparsers()",                       // 11
            "    rsub.add_parser(\"add\").set_defaults(func=remote_add)", // 12：嵌套 + 链式
            "    args = parser.parse_args()",                           // 13
            "    args.func(args)",                                      // 14
            "",                                                         // 15
            "def serve(args):",                                         // 16
            "    pass",                                                 // 17
            "",                                                         // 18
            "def migrate(args):",                                       // 19
            "    pass",                                                 // 20
            "",                                                         // 21
            "def remote_add(args):",                                    // 22：与 cli.py 的同名函数区分开（本文件优先）
            "    pass",                                                 // 23
        ],
        // ── Java：picocli ──
        ["jcli/src/main/java/demo/App.java"] =
        [
            "package demo;",                                                                    // 1
            "import picocli.CommandLine;",                                                      // 2
            "import picocli.CommandLine.Command;",                                              // 3
            "",                                                                                 // 4
            "@Command(name = \"git\", subcommands = {CommitCmd.class, RemoteCmd.class})",       // 5
            "public class App implements Runnable {",                                           // 6
            "    public void run() { }",                                                        // 7：根命令 → route = git
            "    public static void main(String[] args) { new CommandLine(new App()).execute(args); }", // 8
            "}",                                                                                // 9
            "",                                                                                 // 10
            "@Command(name = \"commit\")",                                                      // 11
            "class CommitCmd implements Callable<Integer> {",                                   // 12
            "    public Integer call() { return 0; }",                                          // 13
            "}",                                                                                // 14
            "",                                                                                 // 15
            "@Command(name = \"remote\", subcommands = {RemoteAdd.class})",                     // 16
            "class RemoteCmd {",                                                                // 17：没有 call / run，只有方法子命令
            "    @Command(name = \"prune\")",                                                   // 18
            "    void prune() { }",                                                             // 19：remote prune
            "}",                                                                                // 20
            "",                                                                                 // 21
            "@Command(name = \"add\")",                                                         // 22
            "class RemoteAdd implements Callable<Integer> {",                                   // 23
            "    public Integer call() { return 0; }",                                          // 24：remote add
            "}",                                                                                // 25
        ],
        // ── Node：commander ──
        ["ncli/bin/cli.js"] =
        [
            "const { program } = require('commander');",          // 1
            "const { serve } = require('../lib/serve');",         // 2
            "",                                                   // 3
            "program",                                            // 4
            "  .command('serve <port>')",                         // 5
            "  .description('start server')",                     // 6
            "  .option('-v, --verbose')",                         // 7
            "  .action(serve);",                                  // 8：导入的函数
            "",                                                   // 9
            "const remote = program.command('remote');",          // 10
            "remote",                                             // 11
            "  .command('add <name>')",                           // 12：remote add
            "  .action((name) => addRemote(name));",              // 13：内联 → 第一个能找到的调用
            "",                                                   // 14
            "program.command('install', 'install packages');",    // 15：独立可执行子命令（prog-install.js）→ 没有处理器
            "",                                                   // 16
            "function addRemote(name) {}",                        // 17
            "",                                                   // 18
            "program.action(() => main());",                      // 19：根命令 → route = 容器名
            "function main() {}",                                 // 20
            "program.parse();",                                   // 21
        ],
        ["ncli/lib/serve.js"] =
        [
            "function serve(port) {}",       // 1
            "module.exports = { serve };",   // 2
        ],
        // ── Node：yargs ──
        ["ycli/index.js"] =
        [
            "const yargs = require('yargs');",                                                     // 1
            "const { migrate } = require('./db');",                                                // 2
            "",                                                                                    // 3
            "yargs",                                                                               // 4
            "  .command('serve [port]', 'start the server', (y) => y.positional('port', {}), (argv) => { startServer(argv.port); })", // 5
            "  .command('migrate', 'run migrations', {}, migrate)",                                // 6
            "  .command({ command: 'seed', describe: 'seed data', handler: seedHandler })",       // 7：对象形式，就地注册
            "  .command(require('./cmds/clean'))",                                                 // 8：命令模块
            "  .parse();",                                                                         // 9
            "",                                                                                    // 10
            "function startServer(port) {}",                                                       // 11
            "function seedHandler() {}",                                                           // 12
        ],
        ["ycli/db.js"] =
        [
            "exports.migrate = async function migrate() {};", // 1
        ],
        ["ycli/cmds/clean.js"] =
        [
            "exports.command = 'clean';",                                  // 1
            "exports.describe = 'remove build output';",                   // 2
            "exports.handler = function (argv) { rimraf(argv.dir); };",    // 3
            "function rimraf(dir) {}",                                     // 4
        ],
        ["ycli/cmds/db.js"] =
        [
            "export const command = 'db <action>';",                                      // 1：模块里 .command(Sub) 挂子命令
            "export const builder = (y) => y.command(Seed);",                             // 2
            "export const handler = lazyHandler(() => import('./dbHandler'));",           // 3：懒加载 → 被加载模块的默认导出
            "import * as Seed from './dbSeed';",                                          // 4
        ],
        ["ycli/cmds/dbHandler.js"] =
        [
            "export default async function handler(argv) {}",   // 1
        ],
        ["ycli/cmds/dbSeed.js"] =
        [
            "export const command = 'seed';",                    // 1：db seed
            "export function handler(argv) {}",                  // 2
        ],
    };

    private static readonly List<MapSymbol> CliSymbols =
    [
        // C# System.CommandLine
        Sym(3000, SymbolKind.Class, "Tool.Program", "Tool/Program.cs", null, 3, 22, "public"),
        Sym(3001, SymbolKind.Method, "Tool.Program.Main(string[])", "Tool/Program.cs", 3000, 5, 21, "public"),
        Sym(3002, SymbolKind.Class, "Tool.IndexHandler", "Tool/Handlers.cs", null, 2, 2, "public"),
        Sym(3003, SymbolKind.Method, "Tool.IndexHandler.Run(string)", "Tool/Handlers.cs", 3002, 2, 2, "public"),
        Sym(3004, SymbolKind.Class, "Tool.RemoteOps", "Tool/Handlers.cs", null, 3, 3, "public"),
        Sym(3005, SymbolKind.Method, "Tool.RemoteOps.AddAsync(string)", "Tool/Handlers.cs", 3004, 3, 3, "public"),
        Sym(3006, SymbolKind.Class, "Tool.ServeHandler", "Tool/Handlers.cs", null, 4, 4, "public"),
        Sym(3007, SymbolKind.Method, "Tool.ServeHandler.Process(int)", "Tool/Handlers.cs", 3006, 4, 4, "public"),
        Sym(3008, SymbolKind.Class, "Tool.StatsCommand", "Tool/Handlers.cs", null, 5, 12, "public"),
        Sym(3009, SymbolKind.Method, "Tool.StatsCommand.StatsCommand()", "Tool/Handlers.cs", 3008, 7, 10, "public"),
        Sym(3010, SymbolKind.Method, "Tool.StatsCommand.Execute()", "Tool/Handlers.cs", 3008, 11, 11, "private"),
        // C# Spectre（Program.cs 是顶级语句：Roslyn 给的 <Main>$ 函数符号）
        Sym(3019, SymbolKind.Function, "Program.<Main>$(string[])", "Shell/Program.cs", null, 1, 13, "internal"),
        Sym(3020, SymbolKind.Class, "DefaultCommand", "Shell/Commands.cs", null, 2, 5, "public"),
        Sym(3021, SymbolKind.Method, "DefaultCommand.Execute(CommandContext, Settings)", "Shell/Commands.cs", 3020, 4, 4, "public"),
        Sym(3022, SymbolKind.Class, "AddCommand", "Shell/Commands.cs", null, 6, 9, "public"),
        Sym(3023, SymbolKind.Method, "AddCommand.ExecuteAsync(CommandContext, Settings)", "Shell/Commands.cs", 3022, 8, 8, "public"),
        Sym(3024, SymbolKind.Class, "RemoteAddCommand", "Shell/Commands.cs", null, 10, 13, "public"),
        Sym(3025, SymbolKind.Method, "RemoteAddCommand.Execute(CommandContext, Settings)", "Shell/Commands.cs", 3024, 12, 12, "public"),
        Sym(3026, SymbolKind.Class, "MigrateCommand", "Shell/Commands.cs", null, 14, 17, "public"),
        Sym(3027, SymbolKind.Method, "MigrateCommand.Execute(CommandContext, Settings)", "Shell/Commands.cs", 3026, 16, 16, "public"),
        Sym(3028, SymbolKind.Class, "ListPackagesCommand", "Shell/Commands.cs", null, 18, 21, "public"),
        Sym(3029, SymbolKind.Method, "ListPackagesCommand.Execute(CommandContext, Settings)", "Shell/Commands.cs", 3028, 20, 20, "public"),
        // C# CommandLineParser
        Sym(3040, SymbolKind.Class, "AddOptions", "Clp/Program.cs", null, 3, 3, "internal"),
        Sym(3041, SymbolKind.Class, "CommitOptions", "Clp/Program.cs", null, 5, 5, "internal"),
        Sym(3042, SymbolKind.Class, "CloneOptions", "Clp/Program.cs", null, 7, 7, "internal"),
        Sym(3043, SymbolKind.Class, "Program", "Clp/Program.cs", null, 8, 17, "internal"),
        Sym(3044, SymbolKind.Method, "Program.Main(string[])", "Clp/Program.cs", 3043, 10, 15, "private"),
        Sym(3045, SymbolKind.Method, "Program.RunAdd(AddOptions)", "Clp/Program.cs", 3043, 16, 16, "private"),
        Sym(3046, SymbolKind.Class, "Git", "Clp/Git.cs", null, 1, 5, "internal"),
        Sym(3047, SymbolKind.Method, "Git.Commit(CommitOptions)", "Clp/Git.cs", 3046, 3, 3, "public"),
        Sym(3048, SymbolKind.Method, "Git.Clone(CloneOptions)", "Clp/Git.cs", 3046, 4, 4, "public"),
        // Go cobra
        Sym(3100, SymbolKind.Module, "gocli/cmd/root.go", "gocli/cmd/root.go", null, 1, 17),
        Sym(3101, SymbolKind.Function, "gocli/cmd.init", "gocli/cmd/root.go", 3100, 13, 15),
        Sym(3102, SymbolKind.Function, "gocli/cmd.runServe", "gocli/cmd/root.go", 3100, 17, 17),
        Sym(3103, SymbolKind.Module, "gocli/cmd/remote.go", "gocli/cmd/remote.go", null, 1, 15),
        Sym(3104, SymbolKind.Function, "gocli/cmd.newRemoteCmd", "gocli/cmd/remote.go", 3103, 3, 13),
        Sym(3105, SymbolKind.Function, "gocli/cmd.addRemote", "gocli/cmd/remote.go", 3103, 15, 15),
        // Go urfave
        Sym(3110, SymbolKind.Module, "urf/main.go", "urf/main.go", null, 1, 28),
        Sym(3111, SymbolKind.Function, "urf.main", "urf/main.go", 3110, 5, 25),
        Sym(3112, SymbolKind.Function, "urf.greet", "urf/main.go", 3110, 26, 26),
        Sym(3113, SymbolKind.Function, "urf.serve", "urf/main.go", 3110, 27, 27),
        Sym(3114, SymbolKind.Function, "urf.migrate", "urf/main.go", 3110, 28, 28),
        // Python
        Sym(3200, SymbolKind.Module, "pycli/cli.py", "pycli/cli.py", null, 1, 30),
        Sym(3201, SymbolKind.Function, "pycli.cli.cli", "pycli/cli.py", 3200, 4, 5),
        Sym(3202, SymbolKind.Function, "pycli.cli.init_db", "pycli/cli.py", 3200, 8, 9),
        Sym(3203, SymbolKind.Function, "pycli.cli.remote", "pycli/cli.py", 3200, 12, 13),
        Sym(3204, SymbolKind.Function, "pycli.cli.remote_add", "pycli/cli.py", 3200, 17, 18),
        Sym(3205, SymbolKind.Function, "pycli.cli.do_sync", "pycli/cli.py", 3200, 21, 22),
        Sym(3206, SymbolKind.Function, "pycli.cli.build_command", "pycli/cli.py", 3200, 27, 28),
        Sym(3210, SymbolKind.Module, "pycli/app.py", "pycli/app.py", null, 1, 13),
        Sym(3211, SymbolKind.Function, "pycli.app.hello", "pycli/app.py", 3210, 8, 9),
        Sym(3212, SymbolKind.Function, "pycli.app.goodbye", "pycli/app.py", 3210, 12, 13),
        Sym(3215, SymbolKind.Module, "pycli/users.py", "pycli/users.py", null, 1, 7),
        Sym(3216, SymbolKind.Function, "pycli.users.create_user", "pycli/users.py", 3215, 6, 7),
        Sym(3220, SymbolKind.Module, "pycli/tool.py", "pycli/tool.py", null, 1, 23),
        Sym(3221, SymbolKind.Function, "pycli.tool.main", "pycli/tool.py", 3220, 3, 14),
        Sym(3222, SymbolKind.Function, "pycli.tool.serve", "pycli/tool.py", 3220, 16, 17),
        Sym(3223, SymbolKind.Function, "pycli.tool.migrate", "pycli/tool.py", 3220, 19, 20),
        Sym(3224, SymbolKind.Function, "pycli.tool.remote_add", "pycli/tool.py", 3220, 22, 23),
        // Java picocli
        Sym(3300, SymbolKind.Class, "demo.App", "jcli/src/main/java/demo/App.java", null, 6, 9, "public"),
        Sym(3301, SymbolKind.Method, "demo.App.run()", "jcli/src/main/java/demo/App.java", 3300, 7, 7, "public"),
        Sym(3302, SymbolKind.Method, "demo.App.main(String[])", "jcli/src/main/java/demo/App.java", 3300, 8, 8, "public"),
        Sym(3303, SymbolKind.Class, "demo.CommitCmd", "jcli/src/main/java/demo/App.java", null, 12, 14),
        Sym(3304, SymbolKind.Method, "demo.CommitCmd.call()", "jcli/src/main/java/demo/App.java", 3303, 13, 13, "public"),
        Sym(3305, SymbolKind.Class, "demo.RemoteCmd", "jcli/src/main/java/demo/App.java", null, 17, 20),
        Sym(3306, SymbolKind.Method, "demo.RemoteCmd.prune()", "jcli/src/main/java/demo/App.java", 3305, 19, 19),
        Sym(3307, SymbolKind.Class, "demo.RemoteAdd", "jcli/src/main/java/demo/App.java", null, 23, 25),
        Sym(3308, SymbolKind.Method, "demo.RemoteAdd.call()", "jcli/src/main/java/demo/App.java", 3307, 24, 24, "public"),
        // Node commander
        Sym(3400, SymbolKind.Module, "ncli/bin/cli.js", "ncli/bin/cli.js", null, 1, 21),
        Sym(3401, SymbolKind.Function, "ncli/bin/cli.js:addRemote", "ncli/bin/cli.js", 3400, 17, 17),
        Sym(3402, SymbolKind.Function, "ncli/bin/cli.js:main", "ncli/bin/cli.js", 3400, 20, 20),
        Sym(3403, SymbolKind.Module, "ncli/lib/serve.js", "ncli/lib/serve.js", null, 1, 2),
        Sym(3404, SymbolKind.Function, "ncli/lib/serve.js:serve", "ncli/lib/serve.js", 3403, 1, 1),
        // Node yargs
        Sym(3410, SymbolKind.Module, "ycli/index.js", "ycli/index.js", null, 1, 12),
        Sym(3411, SymbolKind.Function, "ycli/index.js:startServer", "ycli/index.js", 3410, 11, 11),
        Sym(3412, SymbolKind.Function, "ycli/index.js:seedHandler", "ycli/index.js", 3410, 12, 12),
        Sym(3413, SymbolKind.Module, "ycli/db.js", "ycli/db.js", null, 1, 1),
        Sym(3414, SymbolKind.Function, "ycli/db.js:migrate", "ycli/db.js", 3413, 1, 1, "export"),
        Sym(3415, SymbolKind.Module, "ycli/cmds/clean.js", "ycli/cmds/clean.js", null, 1, 4),
        Sym(3416, SymbolKind.Function, "ycli/cmds/clean.js:handler", "ycli/cmds/clean.js", 3415, 3, 3, "export"),
        Sym(3417, SymbolKind.Function, "ycli/cmds/clean.js:rimraf", "ycli/cmds/clean.js", 3415, 4, 4),
        Sym(3418, SymbolKind.Module, "ycli/cmds/db.js", "ycli/cmds/db.js", null, 1, 4),
        Sym(3419, SymbolKind.Function, "ycli/cmds/db.js:builder", "ycli/cmds/db.js", 3418, 2, 2, "export"),
        Sym(3420, SymbolKind.Function, "ycli/cmds/db.js:handler", "ycli/cmds/db.js", 3418, 3, 3, "export"),
        Sym(3421, SymbolKind.Module, "ycli/cmds/dbHandler.js", "ycli/cmds/dbHandler.js", null, 1, 1),
        Sym(3422, SymbolKind.Function, "ycli/cmds/dbHandler.js:handler", "ycli/cmds/dbHandler.js", 3421, 1, 1, "export default"),
        Sym(3423, SymbolKind.Module, "ycli/cmds/dbSeed.js", "ycli/cmds/dbSeed.js", null, 1, 2),
        Sym(3424, SymbolKind.Function, "ycli/cmds/dbSeed.js:handler", "ycli/cmds/dbSeed.js", 3423, 2, 2, "export"),
    ];

    private static readonly List<ArchNode> CliContainers =
    [
        Container("Tool", "dotnet-exe", "Tool"),
        Container("Shell", "dotnet-exe", "Shell"),
        Container("Clp", "dotnet-exe", "Clp"),
        Container("gocli", "go-service", "gocli"),
        Container("urf", "go-service", "urf"),
        Container("pycli", "python-service", "pycli"),
        Container("jcli", "java-service", "jcli"),
        Container("ncli", "node-cli", "ncli"),
        Container("ycli", "node-cli", "ycli"),
    ];

    private static List<EntryPoint> CliEntries() =>
        EntryDiscovery.Discover(CliSymbols, [], CliContainers, p => CliFiles.TryGetValue(p, out var l) ? l : []);

    private static IEnumerable<(string Container, string Route, long Symbol, string File, int Line)> Commands(string ruleId) =>
        CliEntries().Where(e => e.RuleId == ruleId)
            .Select(e => { Assert.Equal(("command", (string?)null), (e.Kind, e.HttpMethod)); return (e.ContainerKey, e.Route!, e.SymbolId, e.File, e.Line); })
            .OrderBy(e => e.ContainerKey, StringComparer.Ordinal).ThenBy(e => e.Item2, StringComparer.Ordinal);

    [Fact]
    public void Cli_CSharp_SystemCommandLine_VariablesSubcommandsAndSubclass()
    {
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("Tool", "index", 3003, "Tool/Handlers.cs", 2),          // SetHandler(IndexHandler.Run)
            ("Tool", "remote add", 3005, "Tool/Handlers.cs", 3),     // lambda → RemoteOps.AddAsync；remote.AddCommand(add) 给出前缀
            ("Tool", "serve", 3007, "Tool/Handlers.cs", 4),          // Handler = CommandHandler.Create(ServeHandler.Process)
            ("Tool", "stats", 3010, "Tool/Handlers.cs", 11),         // class StatsCommand : Command + base("stats") + this.SetHandler(Execute)
        }, Commands("csharp.system-commandline"));
        Assert.DoesNotContain(CliEntries(), e => e.Route is "remote" or "gone" or "demo tool");
    }

    [Fact]
    public void Cli_CSharp_Spectre_AddCommandBranchesDefaultAndUnregisteredSubclass()
    {
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("Shell", "Shell", 3021, "Shell/Commands.cs", 4),             // new CommandApp<DefaultCommand>() → 默认命令，route = 容器名
            ("Shell", "add", 3023, "Shell/Commands.cs", 8),              // AddCommand<AddCommand>("add") → ExecuteAsync
            ("Shell", "db migrate", 3027, "Shell/Commands.cs", 16),      // 单行 lambda 的 AddBranch
            ("Shell", "list-packages", 3029, "Shell/Commands.cs", 20),   // 没注册的 Command<TSettings> 子类：类名推命令名
            ("Shell", "plus", 3023, "Shell/Commands.cs", 8),             // 分支结束后不带前缀
            ("Shell", "remote add", 3025, "Shell/Commands.cs", 12),      // AddBranch("remote", remote => { … }) 的前缀
        }, Commands("csharp.spectre-cli"));
    }

    [Fact]
    public void Cli_CSharp_CommandLineParser_VerbsToMapResultHandlersOrParameterType()
    {
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("Clp", "add", 3045, "Clp/Program.cs", 16),     // (AddOptions opts) => RunAdd(opts)
            ("Clp", "clone", 3048, "Clp/Git.cs", 4),        // MapResult 里没写：参数类型是 CloneOptions 的方法
            ("Clp", "commit", 3047, "Clp/Git.cs", 3),       // (CommitOptions opts) => Git.Commit(opts)
        }, Commands("csharp.commandlineparser"));
    }

    [Fact]
    public void Cli_Go_Cobra_SubcommandPathsThroughConstructorFunctions()
    {
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("gocli", "remote add", 3105, "gocli/cmd/remote.go", 15), // newRemoteCmd() 返回 cmd，cmd.AddCommand(add)；Run 是 func 字面量 → addRemote
            ("gocli", "serve", 3102, "gocli/cmd/root.go", 17),       // RunE: runServe；根命令 tool 不进路径
        }, Commands("go.cobra"));
    }

    [Fact]
    public void Cli_Go_Urfave_CommandsAndSubcommandsLiterals()
    {
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("urf", "db migrate", 3114, "urf/main.go", 28),
            ("urf", "greet", 3112, "urf/main.go", 26),   // App 自己的 Action：route = App 的 Name
            ("urf", "serve", 3113, "urf/main.go", 27),
        }, Commands("go.urfave-cli"));
    }

    [Fact]
    public void Cli_Python_ClickTyperArgparse()
    {
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("pycli", "build", 3206, "pycli/cli.py", 27),         // build_command → build（click 8.2 去掉 _command）
            ("pycli", "init-db", 3202, "pycli/cli.py", 8),
            ("pycli", "remote add", 3204, "pycli/cli.py", 17),    // @remote.command("add")，remote 是 cli 的子 group
            ("pycli", "sync", 3205, "pycli/cli.py", 21),          // @click.command(name="sync") + cli.add_command(do_sync)
        }, Commands("python.click"));
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("pycli", "bye", 3212, "pycli/app.py", 12),
            ("pycli", "hello", 3211, "pycli/app.py", 8),
            ("pycli", "users create-user", 3216, "pycli/users.py", 6), // app.add_typer(users.app, name="users")
        }, Commands("python.typer"));
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("pycli", "migrate", 3223, "pycli/tool.py", 19),      // 变量 p 复用：按出现顺序
            ("pycli", "remote add", 3224, "pycli/tool.py", 22),   // 嵌套 subparsers + 链式 set_defaults；同名函数取本文件的
            ("pycli", "serve", 3222, "pycli/tool.py", 16),
        }, Commands("python.argparse"));
        Assert.DoesNotContain(CliEntries(), e => e.Kind == "command" && e.SymbolId is 3201 or 3203); // group 函数不是入口
    }

    [Fact]
    public void Cli_Java_Picocli_SubcommandsClassesAndMethods()
    {
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("jcli", "commit", 3304, "jcli/src/main/java/demo/App.java", 13),
            ("jcli", "git", 3301, "jcli/src/main/java/demo/App.java", 7),          // 根命令（没有父）的 run
            ("jcli", "remote add", 3308, "jcli/src/main/java/demo/App.java", 24),  // RemoteCmd 的 subcommands
            ("jcli", "remote prune", 3306, "jcli/src/main/java/demo/App.java", 19), // 类里的 @Command 方法
        }, Commands("java.picocli"));
    }

    [Fact]
    public void Cli_Node_Commander_ChainedActionsSubcommandVariablesAndRoot()
    {
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("ncli", "ncli", 3402, "ncli/bin/cli.js", 20),        // program.action(() => main())：根命令
            ("ncli", "remote add", 3401, "ncli/bin/cli.js", 17),  // const remote = program.command('remote'); remote.command('add')
            ("ncli", "serve", 3404, "ncli/lib/serve.js", 1),      // .action(serve)：require 进来的函数
        }, Commands("node.commander"));
        Assert.DoesNotContain(CliEntries(), e => e.Route == "install"); // .command('install', 'desc') 是独立可执行文件
    }

    [Fact]
    public void Cli_Node_Yargs_PositionalObjectAndCommandModules()
    {
        Assert.Equal(new (string, string, long, string, int)[]
        {
            ("ycli", "clean", 3416, "ycli/cmds/clean.js", 3),        // exports.command + exports.handler
            ("ycli", "db", 3422, "ycli/cmds/dbHandler.js", 1),       // lazyHandler(() => import('./dbHandler')) → 默认导出
            ("ycli", "db seed", 3424, "ycli/cmds/dbSeed.js", 2),     // db 模块的 builder 里 .command(Seed)
            ("ycli", "migrate", 3414, "ycli/db.js", 1),              // .command('migrate', desc, {}, migrate)
            ("ycli", "seed", 3412, "ycli/index.js", 12),             // .command({ command, handler })
            ("ycli", "serve", 3411, "ycli/index.js", 11),            // 第四个实参是内联函数 → startServer
        }, Commands("node.yargs"));
    }
}
