using System.Runtime.InteropServices;
using System.Threading;

namespace LlamaRuntime.Native.Contracts;

public static class NativeRuntimeBindings
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReleaseModelDelegate(IntPtr model);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReleaseContextDelegate(IntPtr context);

    private static int _initialized;
    private static ReleaseModelDelegate? _releaseModel;
    private static ReleaseContextDelegate? _releaseContext;

    public static bool IsInitialized => Volatile.Read(ref _initialized) == 1;

    public static void Initialize(IntPtr libraryHandle)
    {
        if (libraryHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Library handle must be non-zero.", nameof(libraryHandle));
        }

        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 1)
        {
            return;
        }

        try
        {
            _releaseModel = LoadFunction<ReleaseModelDelegate>(libraryHandle, "llama_unload_model");
            _releaseContext = LoadFunction<ReleaseContextDelegate>(libraryHandle, "llama_remove_context");
        }
        catch
        {
            Volatile.Write(ref _initialized, 0);
            _releaseModel = null;
            _releaseContext = null;
            throw;
        }
    }

    internal static void ReleaseModel(IntPtr model)
    {
        if (model == IntPtr.Zero || !IsInitialized)
        {
            return;
        }

        _releaseModel?.Invoke(model);
    }

    internal static void ReleaseContext(IntPtr context)
    {
        if (context == IntPtr.Zero || !IsInitialized)
        {
            return;
        }

        _releaseContext?.Invoke(context);
    }

    private static T LoadFunction<T>(IntPtr libraryHandle, string name) where T : Delegate
    {
        var export = NativeLibrary.GetExport(libraryHandle, name);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }
}
