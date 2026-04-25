using System.Reflection;
using System.Runtime.CompilerServices;

using LlamaRuntime.Common.Tests;
using LlamaRuntime.Native;

namespace LlamaRuntime.Engine.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class LlamaNativeTests
{
    [Fact]
    public void GetVersion_WhenDisposed_ThrowsObjectDisposedExceptionForLlamaNative()
    {
        var native = (LlamaNative)RuntimeHelpers.GetUninitializedObject(typeof(LlamaNative));
        typeof(LlamaNative)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(native, true);

        var ex = Assert.IsType<ObjectDisposedException>(Record.Exception(() => native.GetVersion()));

        Assert.Equal(nameof(LlamaNative), ex.ObjectName);
    }
}
