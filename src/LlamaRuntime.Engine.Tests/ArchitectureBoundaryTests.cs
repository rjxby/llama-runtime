using System.Xml.Linq;
using LlamaRuntime.Common.Tests;

namespace LlamaRuntime.Engine.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public class ArchitectureBoundaryTests
{
    public static TheoryData<string, string[]> Dependencies => new()
    {
        { "LlamaRuntime.Native.Contracts", [] },
        { "LlamaRuntime.Native", ["LlamaRuntime.Native.Contracts"] },
        { "LlamaRuntime.Engine.Contracts", ["LlamaRuntime.Native.Contracts"] },
        { "LlamaRuntime.Engine", ["LlamaRuntime.Engine.Contracts", "LlamaRuntime.Native.Contracts"] },
        { "LlamaRuntime.Presentation.Grpc", ["LlamaRuntime.Native", "LlamaRuntime.Engine"] }
    };

    [Theory]
    [MemberData(nameof(Dependencies))]
    public void ProductionProjects_OnlyReferenceTheirDeclaredLayers(string name, string[] allowed)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/llama-runtime.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        var project = XDocument.Load(Path.Combine(root.FullName, "src", name, name + ".csproj"));
        foreach (var reference in project.Descendants("ProjectReference"))
        {
            var dependency = Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace((char)92, '/'));
            Assert.Contains(dependency, allowed);
        }
        if (name != "LlamaRuntime.Presentation.Grpc")
        {
            foreach (var framework in project.Descendants("FrameworkReference"))
            {
                Assert.NotEqual("Microsoft.AspNetCore.App", framework.Attribute("Include")!.Value);
            }
            foreach (var package in project.Descendants("PackageReference"))
            {
                var dependency = package.Attribute("Include")!.Value;
                Assert.False(dependency.StartsWith("Grpc", StringComparison.Ordinal));
                Assert.False(dependency.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
                Assert.NotEqual("Google.Protobuf", dependency);
            }
        }
    }
}
