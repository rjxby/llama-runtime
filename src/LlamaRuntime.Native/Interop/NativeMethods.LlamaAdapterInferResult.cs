using System.Runtime.InteropServices;

namespace LlamaRuntime.Native;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LlamaAdapterInferResult
    {
        public int PromptTokens;
        public int OutputTokens;
        public int TotalTokens;
        public int OutputBytes;
    }
}
