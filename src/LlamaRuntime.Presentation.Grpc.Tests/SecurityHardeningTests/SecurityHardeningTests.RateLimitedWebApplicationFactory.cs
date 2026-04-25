using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using LlamaRuntime.Presentation.Grpc.Configuration;

namespace LlamaRuntime.Presentation.Grpc.Tests;

public sealed partial class SecurityHardeningTests
{
    private sealed class RateLimitedWebApplicationFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["RateLimiter:TokenLimit"] = "1",
                        ["RateLimiter:TokensPerPeriod"] = "1",
                        ["RateLimiter:ReplenishmentPeriod"] = "01:00:00",
                        ["RateLimiter:QueueLimit"] = "0",
                        ["RateLimiter:RejectionStatusCode"] = "429"
                    })
                    .Build();

                services.AddAppRateLimiting(config);
            });

            base.ConfigureWebHost(builder);
        }
    }
}
