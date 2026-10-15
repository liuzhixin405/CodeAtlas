namespace CodeAtlas.Core.Resolution;

/// <summary>
/// Java 源码根与模块根。Maven / Gradle 约定 &lt;模块&gt;/src/&lt;源码集&gt;/java（main、test、integrationTest……）：
/// 源码根到 java 为止，模块根是 src 之前的部分（多模块仓库里每个模块各自一个）。没有约定目录时按 package 声明与目录的对应关系推断
/// （legacy/src/com/x/A.java + package com.x → 源码根 legacy/src），对不上就是文件所在目录；这时模块根等于源码根。
/// <para>
/// 解析器按限定名（= package.类型，来自 package 声明而不是目录）找类型，import a.b.C 就是找限定名 a.b.C 的类型，
/// 等价于"源码根下 a/b/C.java"且不怕目录与 package 不一致；模块根只用来在同一个限定名有多个候选（多模块里重名）时优先同模块。
/// </para>
/// </summary>
public static class JavaSourceRoots
{
    public static string SourceRoot(string relativePath, string? package) => Roots(relativePath, package).Source;

    public static string ModuleRoot(string relativePath, string? package) => Roots(relativePath, package).Module;

    /// <summary>Module 符号的 Signature（"package a.b"）里的包名；没有 package 声明为空串。</summary>
    public static string PackageOf(string? moduleSignature) =>
        moduleSignature is not null && moduleSignature.StartsWith("package ", StringComparison.Ordinal) ? moduleSignature["package ".Length..].Trim() : "";

    private static (string Source, string Module) Roots(string relativePath, string? package)
    {
        var path = relativePath.Replace('\\', '/');
        var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        var segments = dir.Length == 0 ? [] : dir.Split('/');
        for (var i = 0; i + 2 < segments.Length; i++)
        {
            if (segments[i] != "src" || segments[i + 2] != "java") continue;
            return (string.Join('/', segments[..(i + 3)]), string.Join('/', segments[..i]));
        }
        var root = dir;
        if (!string.IsNullOrEmpty(package))
        {
            var packageDir = package.Replace('.', '/');
            if (dir == packageDir) root = "";
            else if (dir.EndsWith("/" + packageDir, StringComparison.Ordinal)) root = dir[..^(packageDir.Length + 1)];
        }
        return (root, root);
    }
}
