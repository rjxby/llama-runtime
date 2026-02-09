using System.Runtime.InteropServices;
using System.Text;

namespace LlamaRuntime.Native;

internal static class NativeMethods
{
    internal const string LibraryLogicalName = "__llama_adapter_native__";

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "llama_adapter_get_version")]
    internal static extern int llama_adapter_get_version(StringBuilder outBuf, UIntPtr outSize);

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "llama_load_model")]
    internal static extern int llama_load_model(string path, out IntPtr modelOut);

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "llama_unload_model")]
    internal static extern int llama_unload_model(IntPtr model);

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "llama_create_context")]
    internal static extern int llama_create_context(IntPtr model, out IntPtr ctxOut);

    [DllImport(LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "llama_remove_context")]
    internal static extern int llama_remove_context(IntPtr ctx);

    [DllImport(NativeMethods.LibraryLogicalName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "llama_infer")]
    internal static extern int llama_infer(IntPtr model, IntPtr ctx, string prompt, StringBuilder outBuf, UIntPtr outSize);
}
