using System.Reflection;
using System.Runtime.CompilerServices;

using LlamaRuntime.Common.Tests;
using LlamaRuntime.Native;
using LlamaRuntime.Native.Contracts;

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

    [Fact]
    public void Infer_InvalidNativeGenerationOptions_ThrowsBeforeNativeCall()
    {
        var native = (LlamaNative)RuntimeHelpers.GetUninitializedObject(typeof(LlamaNative));
        var options = CreateUncheckedNativeGenerationOptions(maxNewTokens: 0, temperature: 0.0f, topP: 1.0f);

        var ex = Assert.IsType<NativeInvalidArgumentException>(Record.Exception(() =>
            native.Infer(
                LlamaContextHandle.FromIntPtr(new IntPtr(1)),
                "prompt",
                generationOptions: options)));

        Assert.Contains("MaxNewTokens", ex.Message);
    }

    private static NativeGenerationOptions CreateUncheckedNativeGenerationOptions(
        int maxNewTokens,
        float temperature,
        float topP)
    {
        var options = (NativeGenerationOptions)RuntimeHelpers.GetUninitializedObject(typeof(NativeGenerationOptions));
        SetBackingField(options, nameof(NativeGenerationOptions.MaxNewTokens), maxNewTokens);
        SetBackingField(options, nameof(NativeGenerationOptions.Temperature), temperature);
        SetBackingField(options, nameof(NativeGenerationOptions.TopP), topP);
        return options;
    }

    private static void SetBackingField<T>(NativeGenerationOptions options, string propertyName, T value)
    {
        typeof(NativeGenerationOptions)
            .GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(options, value);
    }
}
