using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Native;

internal sealed class NativeLoader : IDisposable
{
    private static readonly Lock SyncRoot = new();
    private static IntPtr _sharedHandle;
    private static string? _sharedLibraryPath;
    private static bool _resolverRegistered;

    private readonly string _libraryPath;
    private readonly string _logicalName;
    private readonly ILogger _logger;
    private bool _disposed;

    public NativeLoader(string libraryPath, string logicalName, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logicalName = logicalName ?? throw new ArgumentNullException(nameof(logicalName));
        if (string.IsNullOrWhiteSpace(libraryPath)) throw new ArgumentException(nameof(libraryPath));
        _libraryPath = libraryPath;

        EnsureLoaded();
    }

    public IntPtr Handle
    {
        get
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeLoader));
            }

            return _sharedHandle;
        }
    }

    private void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (_sharedHandle == IntPtr.Zero)
            {
                _sharedHandle = NativeLibrary.Load(_libraryPath);
                _sharedLibraryPath = _libraryPath;
                RegisterResolver();
                _logger.LogInformation("Native library loaded from {Path} as a process-lifetime dependency", _libraryPath);
                return;
            }

            if (!string.Equals(_sharedLibraryPath, _libraryPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Native library already loaded from '{_sharedLibraryPath}'. Loading from '{_libraryPath}' in the same process is not supported.");
            }
        }
    }

    private void RegisterResolver()
    {
        if (_resolverRegistered)
        {
            return;
        }

        var asm = typeof(NativeMethods).Assembly;
        NativeLibrary.SetDllImportResolver(asm, (name, _, _) =>
        {
            if (string.Equals(name, _logicalName, StringComparison.OrdinalIgnoreCase))
            {
                return _sharedHandle;
            }

            return IntPtr.Zero;
        });
        _resolverRegistered = true;
        _logger.LogDebug("DllImport resolver registered once for {Name}", _logicalName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logger.LogDebug("NativeLoader disposed for {Name}; native library remains loaded for process lifetime", _logicalName);
    }
}
