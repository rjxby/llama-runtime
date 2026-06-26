namespace LlamaRuntime.Native.Contracts;

public enum NativeError : int
{
    Ok = 0,
    InvalidArgument = 1,
    NotInitialized = 2,
    LoadModel = 3,
    OutOfMemory = 4,
    Infer = 5,
    NotImplemented = 6,
    NotFound = 7,
    Io = 8,
    BufferTooSmall = 9,
    EmptyOutput = 10,
    Cancelled = 11,
    Unknown = 100
}
