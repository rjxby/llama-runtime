namespace LlamaRuntime.Presentation.Grpc.Configuration;

public class RateLimiterOptions
{
    public const string SectionName = "RateLimiter";

    public int TokenLimit { get; set; } = 20;
    public int TokensPerPeriod { get; set; } = 5;
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);
    public int QueueLimit { get; set; } = 0;
    public int RejectionStatusCode { get; set; } = 429;
    public string ApiKeyHeaderName { get; set; } = "x-api-key";
}
