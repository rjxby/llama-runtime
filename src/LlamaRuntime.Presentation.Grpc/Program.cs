using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.Services;
using LlamaRuntime.Presentation.Grpc.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHostedRuntime();
builder.Services.AddApiKeyAuth();
builder.Services.AddAppRateLimiting(builder.Configuration);
builder.Services.AddLlamaHealthChecks();

builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<LoggingInterceptor>();
});

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGrpcService<GeneratorService>();
app.MapLlamaHealthChecks();

app.Run();
