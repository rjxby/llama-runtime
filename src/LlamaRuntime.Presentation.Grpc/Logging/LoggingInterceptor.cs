using Grpc.Core;
using Grpc.Core.Interceptors;
using System.Diagnostics;

namespace LlamaRuntime.Presentation.Grpc.Logging;

public class LoggingInterceptor : Interceptor
{
    private readonly ILogger<LoggingInterceptor> _logger;

    public LoggingInterceptor(ILogger<LoggingInterceptor> logger)
    {
        _logger = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var methodName = context.Method;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("gRPC Request started: {Method}", methodName);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("gRPC Request Payload: {Request}", request);
        }

        try
        {
            var response = await continuation(request, context);
            stopwatch.Stop();

            _logger.LogInformation("gRPC Request finished: {Method} in {ElapsedMs}ms", methodName, stopwatch.ElapsedMilliseconds);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("gRPC Response Payload: {Response}", response);
            }

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "gRPC Request failed: {Method} after {ElapsedMs}ms", methodName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
