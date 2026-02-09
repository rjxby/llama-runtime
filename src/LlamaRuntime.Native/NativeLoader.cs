using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Native;

internal sealed class NativeLoader : IDisposable
{
    private readonly IntPtr _handle;
    private readonly string _logicalName;
    private readonly ILogger _logger;
    private bool _disposed;

    public NativeLoader(string libraryPath, string logicalName, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logicalName = logicalName ?? throw new ArgumentNullException(nameof(logicalName));
        if (string.IsNullOrWhiteSpace(libraryPath)) throw new ArgumentException(nameof(libraryPath));

        _handle = NativeLibrary.Load(libraryPath);
        RegisterResolver();
        _logger.LogInformation("Native library loaded from {Path}", libraryPath);
    }

    private void RegisterResolver()
    {
        var asm = typeof(NativeMethods).Assembly;
        NativeLibrary.SetDllImportResolver(asm, (name, assembly, searchPath) =>
        {
            if (string.Equals(name, _logicalName, StringComparison.OrdinalIgnoreCase))
                return _handle;
            return IntPtr.Zero;
        });
        _logger.LogDebug("DllImport resolver registered for {Name}", _logicalName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // not freeing handle explicitly: process exit will free. If explicit free is required, call NativeLibrary.Free(handle).
        _logger.LogDebug("NativeLoader disposed for {Name}", _logicalName);
    }
}
