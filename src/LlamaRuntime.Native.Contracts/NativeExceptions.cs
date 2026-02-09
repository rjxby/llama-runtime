namespace LlamaRuntime.Native.Contracts;

public class NativeException : Exception
{
    public NativeError NativeErrorCode { get; }
    public NativeException(NativeError code, string message) : base(message) => NativeErrorCode = code;
}

public class NativeInvalidArgumentException : NativeException
{
    public NativeInvalidArgumentException(string m) : base(NativeError.InvalidArgument, m) { }
}

public class NativeNotInitializedException : NativeException
{
    public NativeNotInitializedException(string m) : base(NativeError.NotInitialized, m) { }
}

public class NativeLoadModelException : NativeException
{
    public NativeLoadModelException(string m) : base(NativeError.LoadModel, m) { }
}

public class NativeOutOfMemoryException : NativeException
{
    public NativeOutOfMemoryException(string m) : base(NativeError.OutOfMemory, m) { }
}

public class NativeInferException : NativeException
{
    public NativeInferException(string m) : base(NativeError.Infer, m) { }
}

public class NativeNotImplementedException : NativeException
{
    public NativeNotImplementedException(string m) : base(NativeError.NotImplemented, m) { }
}

public class NativeNotFoundException : NativeException
{
    public NativeNotFoundException(string m) : base(NativeError.NotFound, m) { }
}

public class NativeIOException : NativeException
{
    public NativeIOException(string m) : base(NativeError.Io, m) { }
}

public class NativeUnknownException : NativeException
{
    public NativeUnknownException(string m) : base(NativeError.Unknown, m) { }
}
