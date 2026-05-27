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

    // Teams DOM selectors — new Teams (v2) takes priority; classic Teams fallbacks follow
    public const string TeamsBaseUrl             = "https://teams.microsoft.com/v2/";
    public const string MessageContainerSelector = "[data-tid='message-pane-list-item'], [data-tid='chat-pane-message'], [role='listitem'][data-mid], [role='article']";
    public const string MessageIdAttribute       = "data-mid";            // new Teams; fall back to data-message-id
    public const string ReplyToAttribute         = "data-reply-chain-id"; // new Teams; fall back to data-reply-to
    public const string AuthorSelector           = "[data-tid*='author'], [data-tid='message-author-name']";
    public const string AuthorFallbackSelector   = "[data-testid='message-author'], .author-name";
    public const string TimestampSelector        = "time[datetime]";
    public const string ContentSelector          = "[data-tid='message-body'], [data-tid*='message-body']";
    public const string ContentFallbackSelector  = "[data-testid='message-body'], .message-body";

    // Category keywords (lowercase for case-insensitive matching)
    public static readonly string[] DatabaseKeywords    = ["sql", "database", "db", "query", "schema", "migration", "connection pool", "entity framework", "orm"];
    public static readonly string[] ApiKeywords         = ["api", "endpoint", "rest", "http", "request", "response", "swagger", "openapi", "graphql", "webhook"];
    public static readonly string[] DeploymentKeywords  = ["deploy", "pipeline", "ci/cd", "release", "build", "artifact", "docker", "kubernetes", "k8s", "helm"];
    public static readonly string[] PerformanceKeywords = ["slow", "performance", "timeout", "memory", "cpu", "latency", "cache", "throughput", "bottleneck", "leak"];
    public static readonly string[] InfraKeywords       = ["server", "vm", "cloud", "azure", "aws", "network", "firewall", "dns", "load balancer", "certificate"];
    public static readonly string[] SecurityKeywords    = ["auth", "token", "certificate", "ssl", "tls", "password", "permission", "rbac", "oauth", "vulnerability"];
}
