namespace TeamsChannelScraper.WPF.Utilities;

public static class Constants
{
    // App storage
    public const string AppDataFolder    = "TeamsChannelScraper";
    public const string SessionFileName  = "session.json";
    public const string LogFolder        = "logs";
    public const string ExportsRootFolder = "TeamsExports";
    public const int    SessionMaxAgeDays = 30;

    // Scraping defaults
    public const int DefaultMaxMessages  = 500;
    public const int MaxScrollAttempts   = 150;
    public const int ScrollPauseMs       = 2000;
    public const int MaxRetryAttempts    = 3;
    public const int RetryBaseDelayMs    = 500;

    // Teams DOM selectors (volatile — update when Teams HTML changes)
    public const string MessageContainerSelector = "[role='article']";
    public const string MessageIdAttribute       = "data-message-id";
    public const string ReplyToAttribute         = "data-reply-to";
    public const string AuthorSelector           = "[data-testid='message-author']";
    public const string AuthorFallbackSelector   = ".author-name";
    public const string TimestampSelector        = "time[datetime]";
    public const string ContentSelector          = "[data-testid='message-body']";
    public const string ContentFallbackSelector  = ".message-body";

    // Category keywords (lowercase for case-insensitive matching)
    public static readonly string[] DatabaseKeywords    = ["sql", "database", "db", "query", "schema", "migration", "connection pool", "entity framework", "orm"];
    public static readonly string[] ApiKeywords         = ["api", "endpoint", "rest", "http", "request", "response", "swagger", "openapi", "graphql", "webhook"];
    public static readonly string[] DeploymentKeywords  = ["deploy", "pipeline", "ci/cd", "release", "build", "artifact", "docker", "kubernetes", "k8s", "helm"];
    public static readonly string[] PerformanceKeywords = ["slow", "performance", "timeout", "memory", "cpu", "latency", "cache", "throughput", "bottleneck", "leak"];
    public static readonly string[] InfraKeywords       = ["server", "vm", "cloud", "azure", "aws", "network", "firewall", "dns", "load balancer", "certificate"];
    public static readonly string[] SecurityKeywords    = ["auth", "token", "certificate", "ssl", "tls", "password", "permission", "rbac", "oauth", "vulnerability"];
}
