namespace TeamsChannelScraper.WPF.Utilities;

public sealed class TeamsScraperException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class SessionExpiredException(string message)
    : TeamsScraperException(message);

public sealed class ExportException(string message, Exception? inner = null)
    : Exception(message, inner);
