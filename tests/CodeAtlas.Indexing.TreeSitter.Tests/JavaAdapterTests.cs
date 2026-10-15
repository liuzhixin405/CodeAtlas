using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// Java 适配器：符号（限定名按 package 命名空间风格、方法带参数类型简名、StartLine 跳过注解）与引用绑定。
/// 夹具 Fixtures/java 的说明见 <see cref="JavaResolutionTests"/>。
/// </summary>
public class JavaAdapterTests
{
    private static string Src(string rel) => File.ReadAllText(Path.Combine(TypeScriptAdapterTests.Fixture("java"), rel.Replace('/', Path.DirectorySeparatorChar)));

    private static ParsedFile Parse(string rel, string? source = null)
    {
        using var adapter = new JavaAdapter();
        return adapter.Parse(rel, source ?? Src(rel));
    }

    private static string? ParentQ(ParsedFile f, ParsedSymbol s) => s.ParentIndex is { } p ? f.Symbols[p].QualifiedName : null;

    private const string Controller = "app/src/main/java/com/shop/app/web/OwnerController.java";

    // ---------- 符号 ----------

    [Fact]
    public void Symbols_PackageQualified_MethodsCarryParameterTypes_StartLineSkipsAnnotations()
    {
        var f = Parse(Controller);
        Assert.Equal("java", f.Language);
        Assert.Equal((SymbolKind.Module, "OwnerController.java", Controller, "package com.shop.app.web"),
            (f.Symbols[0].Kind, f.Symbols[0].Name, f.Symbols[0].QualifiedName, f.Symbols[0].Signature));
        var actual = f.Symbols.Skip(1)
            .Select(s => (s.Kind, s.Name, s.QualifiedName, Parent: ParentQ(f, s), s.Visibility, s.StartLine, s.EndLine))
            .ToList();
        Assert.Equal(new[]
        {
            (SymbolKind.Class, "OwnerController", "com.shop.app.web.OwnerController", (string?)Controller, (string?)"public", 11, 37),   // @RestController 在第 10 行
            (SymbolKind.Field, "service", "com.shop.app.web.OwnerController.service", "com.shop.app.web.OwnerController", "private", 13, 13), // @Autowired 在第 12 行
            (SymbolKind.Method, "show", "com.shop.app.web.OwnerController.show(String)", "com.shop.app.web.OwnerController", "public", 18, 24), // @GetMapping 跨 15–17 行
            (SymbolKind.Method, "create", "com.shop.app.web.OwnerController.create(Owner)", "com.shop.app.web.OwnerController", "public", 26, 31),
            (SymbolKind.Method, "audit", "com.shop.app.web.OwnerController.audit(Owner)", "com.shop.app.web.OwnerController", "private", 33, 36),
        }, actual);
        // Signature：去掉注解；字段记 修饰符 + 声明类型 + 名字（解析器从这里读字段类型）
        Assert.Equal(new[]
        {
            "public class OwnerController",
            "private OwnerService service",
            "public Owner show(String name)",
            "public void create(Owner owner)",
            "private void audit(Owner owner)",
        }, f.Symbols.Skip(1).Select(s => s.Signature));
    }

    private const string Box = """
        import java.util.*;
        import static org.x.Assert.*;
        import static org.x.Assert.check;

        @Deprecated
        public class Box<T> extends Base<T> implements Comparable<Box<T>>, java.io.Serializable {
            int a, b;
            private static final String NAME = "box";
            protected List<Item> items = new ArrayList<>();

            Box(int a) {
                this.a = a;
            }

            public <R> R map(T value, R[] out, Map<String, List<Item>> index, String... rest) {
                for (Item it : items) {
                    it.use();
                }
                items.forEach(i -> i.use());
                ((Item) value).use();
                super.close();
                check(rest.length);
                items.stream().map(Item::name);
                return null;
            }

            enum Color { RED, GREEN; Color next() { return RED; } }

            record Pair(Item left, int right) {
                Pair {
                }
                Item first() { return left.copy(); }
            }

            interface Visitor {
                int LIMIT = 3;
                void visit(Item item);
            }

            @interface Marker {
                String value() default "";
            }
        }
        """;

    [Fact]
    public void Symbols_NoPackage_NestedEnumRecordInterfaceAnnotation_ConstructorsAndVisibilityDefaults()
    {
        var f = Parse("src/Box.java", Box);
        Assert.Null(f.Symbols[0].Signature); // 没有 package 声明
        var actual = f.Symbols.Skip(1).Select(s => (s.Kind, s.QualifiedName, s.Visibility, s.StartLine, s.EndLine)).ToList();
        Assert.Equal(new[]
        {
            (SymbolKind.Class, "Box", (string?)"public", 6, 43),
            (SymbolKind.Field, "Box.a", "package", 7, 7),                       // 缺省可见性记 "package"
            (SymbolKind.Field, "Box.b", "package", 7, 7),
            (SymbolKind.Field, "Box.NAME", "private", 8, 8),
            (SymbolKind.Field, "Box.items", "protected", 9, 9),
            (SymbolKind.Method, "Box.Box(int)", "package", 11, 13),             // 构造函数 = <Type>.<Type>(…)
            (SymbolKind.Method, "Box.map(T,R[],Map,String...)", "public", 15, 25), // 参数类型简名：去泛型实参，保留 [] 与 ...
            (SymbolKind.Enum, "Box.Color", "package", 27, 27),
            (SymbolKind.Method, "Box.Color.next()", "package", 27, 27),
            (SymbolKind.Class, "Box.Pair", "package", 29, 33),                  // record → Class
            (SymbolKind.Field, "Box.Pair.left", "private", 29, 29),             // 记录组件是 private 字段
            (SymbolKind.Field, "Box.Pair.right", "private", 29, 29),
            (SymbolKind.Method, "Box.Pair.Pair(Item,int)", "package", 30, 31),  // 紧凑构造函数取记录头的参数
            (SymbolKind.Method, "Box.Pair.first()", "package", 32, 32),
            (SymbolKind.Interface, "Box.Visitor", "package", 35, 38),
            (SymbolKind.Field, "Box.Visitor.LIMIT", "public", 36, 36),          // 接口成员隐式 public
            (SymbolKind.Method, "Box.Visitor.visit(Item)", "public", 37, 37),
            (SymbolKind.Interface, "Box.Marker", "package", 40, 42),            // @interface → Interface
            (SymbolKind.Method, "Box.Marker.value()", "public", 41, 41),
        }, actual);
        Assert.Equal("public <R> R map(T value, R[] out, Map<String, List<Item>> index, String... rest)", f.Symbols.Single(s => s.Name == "map").Signature);
        Assert.Equal("public class Box<T> extends Base<T> implements Comparable<Box<T>>, java.io.Serializable", f.Symbols[1].Signature);
        Assert.Equal("protected List<Item> items", f.Symbols.Single(s => s.Name == "items").Signature);
        Assert.Equal("private Item left", f.Symbols.Single(s => s.Name == "left").Signature);
        Assert.All(f.Symbols.Select((s, i) => (s, i)), x => Assert.True(x.s.ParentIndex is null || x.s.ParentIndex < x.i));
    }

    // ---------- 引用 ----------

    [Fact]
    public void Imports_PlainPaths_WildcardAndStatic()
    {
        Assert.Equal(new[] { ("java.util.*", false, 1), ("org.x.Assert.*", false, 2), ("org.x.Assert.check", false, 3) },
            Parse("src/Box.java", Box).Imports.Select(i => (i.Target, i.IsPath, i.Line)));
        var controller = Parse(Controller);
        Assert.Equal(new[]
        {
            ("org.springframework.beans.factory.annotation.Autowired", 3), ("org.springframework.web.bind.annotation.GetMapping", 4),
            ("org.springframework.web.bind.annotation.RestController", 5), ("com.shop.app.model.Owner", 7), ("com.shop.app.service.OwnerService", 8),
        }, controller.Imports.Select(i => (i.Target, i.Line)));
        // 每条 import 也是一条从 Module 出发的 imports 引用
        Assert.Equal(controller.Imports.Select(i => (0, i.Target, i.Line)),
            controller.References.Where(r => r.Kind == EdgeKind.Imports).Select(r => (r.FromIndex, r.Name, r.Line)));
    }

    [Fact]
    public void Calls_ReceiverTypeFromFieldLocalVarAndParameter_ArgumentCounts()
    {
        var f = Parse(Controller);
        var calls = f.References.Where(r => r.Kind == EdgeKind.Calls)
            .Select(r => (From: f.Symbols[r.FromIndex].Name, r.Name, r.Line, r.Script)).ToList();
        Assert.Equal(new[]
        {
            ("show", "find", 19, (ScriptBinding?)new ScriptBinding(ScriptReceiver.Other, "OwnerService", "1")),   // 字段 @Autowired OwnerService service
            ("show", "Address", 20, new ScriptBinding(ScriptReceiver.None, "Owner.Address", "0")),               // new Owner.Address()
            ("show", "format", 21, new ScriptBinding(ScriptReceiver.Other, "Owner.Address", "0")),               // var address = new Owner.Address()
            ("show", "find", 22, new ScriptBinding(ScriptReceiver.Other, "OwnerService", "1")),
            ("show", "getLastName", 22, new ScriptBinding(ScriptReceiver.Other, "()OwnerService", "0")),         // 调用链的结果：记链根的类型
            ("create", "isNew", 27, new ScriptBinding(ScriptReceiver.Other, "Owner", "0")),                      // 参数 Owner owner
            ("create", "save", 28, new ScriptBinding(ScriptReceiver.Other, "OwnerService", "1")),
            ("create", "audit", 30, new ScriptBinding(ScriptReceiver.This, null, "1")),                          // this.audit(owner)
            ("audit", "format", 35, new ScriptBinding(ScriptReceiver.Other, "Owner.Address", "0")),              // Owner.Address a
        }, calls);
    }

    [Fact]
    public void Calls_EnhancedFor_Lambda_Cast_Super_StaticImport_MethodReference_FieldInitializer_RecordComponent()
    {
        var f = Parse("src/Box.java", Box);
        var calls = f.References.Where(r => r.Kind == EdgeKind.Calls)
            .Select(r => (From: f.Symbols[r.FromIndex].QualifiedName, r.Name, r.Line, r.Script)).ToList();
        Assert.Equal(new[]
        {
            ("Box.items", "ArrayList", 9, (ScriptBinding?)new ScriptBinding(ScriptReceiver.None, "ArrayList", "0")), // 字段初始化器里的调用归到字段
            ("Box.map(T,R[],Map,String...)", "use", 17, new ScriptBinding(ScriptReceiver.Other, "Item", "0")),      // for (Item it : items)
            ("Box.map(T,R[],Map,String...)", "forEach", 19, new ScriptBinding(ScriptReceiver.Other, "List", "1")),  // 字段 List<Item> items：去泛型
            ("Box.map(T,R[],Map,String...)", "use", 19, new ScriptBinding(ScriptReceiver.Other, null, "0")),        // lambda 参数 i：类型未知
            ("Box.map(T,R[],Map,String...)", "use", 20, new ScriptBinding(ScriptReceiver.Other, "Item", "0")),      // ((Item) value).use()
            ("Box.map(T,R[],Map,String...)", "close", 21, new ScriptBinding(ScriptReceiver.This, "super", "0")),
            ("Box.map(T,R[],Map,String...)", "check", 22, new ScriptBinding(ScriptReceiver.This, null, "1")),       // 裸调用（可能来自 static import）
            ("Box.map(T,R[],Map,String...)", "stream", 23, new ScriptBinding(ScriptReceiver.Other, "List", "0")),
            ("Box.map(T,R[],Map,String...)", "map", 23, new ScriptBinding(ScriptReceiver.Other, "()List", "1")),   // items.stream().map：链根是字段 items
            ("Box.map(T,R[],Map,String...)", "name", 23, new ScriptBinding(ScriptReceiver.Import, "Item", "-1")),   // Item::name：接收者是名字，实参个数不限
            ("Box.Pair.first()", "copy", 32, new ScriptBinding(ScriptReceiver.Other, "Item", "0")),                 // 记录组件 Item left
        }, calls);
    }

    [Fact]
    public void TypeReferences_FieldParamReturnLocal_GenericArguments_TypeParametersAndPrimitivesSkipped()
    {
        var f = Parse("src/Box.java", Box);
        string From(ParsedReference r) => f.Symbols[r.FromIndex].QualifiedName;
        Assert.Equal(new[]
        {
            ("Box", "Base", EdgeKind.Inherits, 6, (ScriptBinding?)new ScriptBinding(ScriptReceiver.None, "Base")),
            ("Box", "Comparable", EdgeKind.Implements, 6, new ScriptBinding(ScriptReceiver.None, "Comparable")),
            ("Box", "Serializable", EdgeKind.Implements, 6, new ScriptBinding(ScriptReceiver.None, "java.io.Serializable")),
        }, f.References.Where(r => r.Kind is EdgeKind.Inherits or EdgeKind.Implements).Select(r => (From(r), r.Name, r.Kind, r.Line, r.Script)));
        // T / R 是类型参数，int / void 是基本类型：都不记
        Assert.Equal(new[]
        {
            ("Box.NAME", "String", 8), ("Box.items", "List", 9), ("Box.items", "Item", 9),
            ("Box.map(T,R[],Map,String...)", "Map", 15), ("Box.map(T,R[],Map,String...)", "String", 15), ("Box.map(T,R[],Map,String...)", "List", 15),
            ("Box.map(T,R[],Map,String...)", "Item", 15), ("Box.map(T,R[],Map,String...)", "Item", 16),
            ("Box.Color.next()", "Color", 27), ("Box.Pair.left", "Item", 29), ("Box.Pair.first()", "Item", 32),
            ("Box.Visitor.visit(Item)", "Item", 37), ("Box.Marker.value()", "String", 41),
        }, f.References.Where(r => r.Kind == EdgeKind.References).Select(r => (From(r), r.Name, r.Line)));
        var controller = Parse(Controller);
        var scoped = controller.References.Single(r => r.Kind == EdgeKind.References && r.Line == 34);
        Assert.Equal(("Address", new ScriptBinding(ScriptReceiver.None, "Owner.Address")), (scoped.Name, scoped.Script)); // Owner.Address a：记整段写法
    }
}
