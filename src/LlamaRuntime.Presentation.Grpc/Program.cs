using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLlamaCore();
builder.Services.AddManagers();
builder.Services.AddHostedModel();
builder.Services.AddApiKeyAuth();
builder.Services.AddAppRateLimiting(builder.Configuration);
builder.Services.AddLlamaHealthChecks();

builder.Services.AddGrpc();

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGrpcService<GeneratorService>();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = hc => hc.Name == "model_ready"
});

app.Run();
