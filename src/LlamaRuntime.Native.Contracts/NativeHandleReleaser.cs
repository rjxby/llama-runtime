namespace LlamaRuntime.Native.Contracts;

public static class NativeHandleReleaser
{
    public static Action<IntPtr>? ReleaseModel { get; private set; }
    public static Action<IntPtr>? ReleaseContext { get; private set; }

    public static void Register(Action<IntPtr>? releaseModel, Action<IntPtr>? releaseContext)
    {
        ReleaseModel = releaseModel;
        ReleaseContext = releaseContext;
    }
}
