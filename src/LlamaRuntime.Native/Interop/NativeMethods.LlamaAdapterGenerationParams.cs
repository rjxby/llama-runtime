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
        public AbortCallback? AbortCallback;
        public IntPtr AbortCallbackData;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool AbortCallback(IntPtr data);
}
