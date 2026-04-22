using System.Runtime.InteropServices;
using System.Text;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Native;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LlamaAdapterGenerationParams
    {
        public int MaxNewTokens;
        public float Temperature;
        public float TopP;
        public uint Seed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LlamaAdapterInferResult
    {
        public int PromptTokens;
        public int OutputTokens;
        public int TotalTokens;
        public int OutputBytes;
    }

    internal const string LibraryLogicalName = "__llama_adapter_native__";

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "llama_adapter_get_version")]
    internal static extern int llama_adapter_get_version(StringBuilder outBuf, UIntPtr outSize);

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "llama_load_model")]
    internal static extern int llama_load_model(string path, out IntPtr modelOut);

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "llama_unload_model")]
    internal static extern int llama_unload_model(IntPtr model);

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "llama_create_context")]
    internal static extern int llama_create_context(LlamaModelHandle model, int nCtx, int nBatch, int generationMaxNewTokens, out IntPtr ctxOut);

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "llama_remove_context")]
    internal static extern int llama_remove_context(IntPtr ctx);

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "llama_context_reset")]
    internal static extern int llama_context_reset(LlamaContextHandle ctx);

    [DllImport(NativeMethods.LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "llama_count_tokens")]
    internal static extern int llama_count_tokens(LlamaContextHandle ctx, string prompt, out int tokenCount);

    [DllImport(NativeMethods.LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "llama_infer")]
    internal static extern int llama_infer(
        LlamaContextHandle ctx,
        string prompt,
        in LlamaAdapterGenerationParams parameters,
        StringBuilder outBuf,
        UIntPtr outSize,
        out LlamaAdapterInferResult result);
}
