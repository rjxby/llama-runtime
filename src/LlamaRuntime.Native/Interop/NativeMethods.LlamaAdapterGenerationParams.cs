using System.Runtime.InteropServices;

namespace LlamaRuntime.Native;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LlamaAdapterGenerationParams
    {
        public int MaxNewTokens;
        public float Temperature;
        public float TopP;
        public uint Seed;
        public int ResponseFormat;
        public IntPtr Grammar;
    }
}
