using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class GenerationOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InferenceGenerationOptions_InvalidMaxOutputTokens_Throws(int maxOutputTokens)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InferenceGenerationOptions(maxOutputTokens, 0.0f, 1.0f));

        Assert.Equal("maxOutputTokens", ex.ParamName);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InferenceGenerationOptions_InvalidTemperature_Throws(float temperature)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InferenceGenerationOptions(1, temperature, 1.0f));

        Assert.Equal("temperature", ex.ParamName);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InferenceGenerationOptions_InvalidTopP_Throws(float topP)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InferenceGenerationOptions(1, 0.7f, topP));

        Assert.Equal("topP", ex.ParamName);
    }

    [Fact]
    public void InferenceGenerationOptions_NonDefaultTopPWithGreedyTemperature_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new InferenceGenerationOptions(1, 0.0f, 0.8f));

        Assert.Equal("topP", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NativeGenerationOptions_InvalidMaxNewTokens_Throws(int maxNewTokens)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeGenerationOptions(maxNewTokens, 0.0f, 1.0f));

        Assert.Equal("maxNewTokens", ex.ParamName);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NativeGenerationOptions_InvalidTemperature_Throws(float temperature)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeGenerationOptions(1, temperature, 1.0f));

        Assert.Equal("temperature", ex.ParamName);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NativeGenerationOptions_InvalidTopP_Throws(float topP)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeGenerationOptions(1, 0.7f, topP));

        Assert.Equal("topP", ex.ParamName);
    }

    [Fact]
    public void NativeGenerationOptions_NonDefaultTopPWithGreedyTemperature_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new NativeGenerationOptions(1, 0.0f, 0.8f));

        Assert.Equal("topP", ex.ParamName);
    }
}
