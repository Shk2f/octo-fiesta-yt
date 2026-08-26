namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the YouTube provider.
/// </summary>
public class YouTubeSettings
{
    /// <summary>
    /// Enables the YouTube provider.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path to the yt-dlp binary.
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Maximum number of search results returned by the provider.
    /// </summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>
    /// Preferred audio format (e.g. m4a, mp3).
    /// </summary>
    public string AudioFormat { get; set; } = "m4a";

    /// <summary>
    /// Optional path to cookies file for authenticated requests.
    /// </summary>
    public string? CookiesPath { get; set; }

    /// <summary>
    /// Maximum number of yt-dlp processes allowed to run at once across the whole app.
    /// Each yt-dlp invocation shells out to Python (and, for searches, spawns its own HTTP
    /// workers), so an unbounded burst of metadata-enrichment lookups - a client browsing the
    /// library or re-syncing favourites fires getAlbum/getArtist for hundreds of items at once -
    /// can spawn hundreds of processes and exhaust host RAM + swap, triggering an OOM kill.
    /// </summary>
    public int MaxConcurrentProcesses { get; set; } = 4;

    /// <summary>
    /// Hard timeout, in seconds, for a single yt-dlp metadata/search invocation. A process that
    /// hangs (YouTube throttling, network stall) is killed once this elapses so its concurrency
    /// slot is freed instead of being held forever.
    /// </summary>
    public int ProcessTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Hard timeout, in seconds, for a single yt-dlp track download. Downloads legitimately take
    /// longer than metadata lookups, so they get their own, larger budget.
    /// </summary>
    public int DownloadTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// How long, in seconds, a queued yt-dlp invocation waits for a free concurrency slot before
    /// giving up. Keeps request-handling threads from piling up without bound when every slot is
    /// busy; the caller then degrades gracefully (e.g. returns local-only results).
    /// </summary>
    public int ProcessQueueTimeoutSeconds { get; set; } = 45;
}
