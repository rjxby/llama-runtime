using System.Runtime.InteropServices;

namespace LlamaRuntime.Native;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LlamaAdapterModelMetadata
    {
        public int TrainingContextSize;
        public int TokenizerType;
    }
}
