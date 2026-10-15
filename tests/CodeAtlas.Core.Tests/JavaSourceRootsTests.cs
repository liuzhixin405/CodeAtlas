using CodeAtlas.Core.Resolution;

namespace CodeAtlas.Core.Tests;

public class JavaSourceRootsTests
{
    [Theory]
    // Maven / Gradle 约定目录：模块根是约定目录之前的部分（仓库根的单模块为 ""）
    [InlineData("src/main/java/org/x/A.java", "org.x", "src/main/java", "")]
    [InlineData("src/test/java/org/x/ATest.java", "org.x", "src/test/java", "")]
    [InlineData("customers-service/src/main/java/org/x/A.java", "org.x", "customers-service/src/main/java", "customers-service")]
    [InlineData("svc/a/src/integrationTest/java/org/x/A.java", "org.x", "svc/a/src/integrationTest/java", "svc/a")]
    // 没有约定目录：按 package 与目录的对应关系推断
    [InlineData("legacy/src/com/legacy/Owner.java", "com.legacy", "legacy/src", "legacy/src")]
    [InlineData("com/legacy/Owner.java", "com.legacy", "", "")]
    // package 与目录对不上 / 没有 package：文件所在目录
    [InlineData("scripts/Tool.java", "org.tools", "scripts", "scripts")]
    [InlineData("Main.java", "", "", "")]
    public void SourceAndModuleRoots(string path, string package, string sourceRoot, string moduleRoot)
    {
        Assert.Equal(sourceRoot, JavaSourceRoots.SourceRoot(path, package));
        Assert.Equal(moduleRoot, JavaSourceRoots.ModuleRoot(path, package));
    }

    [Theory]
    [InlineData("package org.x.y", "org.x.y")]
    [InlineData(null, "")]
    [InlineData("class A", "")]
    public void PackageFromModuleSignature(string? signature, string expected) =>
        Assert.Equal(expected, JavaSourceRoots.PackageOf(signature));
}
