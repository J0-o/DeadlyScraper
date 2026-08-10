using System.Net;
using System.Text.Json.Serialization;

namespace DeadlyScraper;

public sealed class DeadlyStreamFileMetadata
{
    public required string SourceUrl { get; init; }
    [JsonIgnore]
    public string? DownloadPageUrl { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? LatestVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedVersion { get; init; }
    public string? LatestVersionReleaseDate { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedVersionReleaseDate { get; init; }
    public string? OriginalUploadDate { get; init; }
    [JsonIgnore]
    public IReadOnlyList<DeadlyStreamVersionInfo> VersionHistory { get; init; } = Array.Empty<DeadlyStreamVersionInfo>();
    [JsonPropertyName("VersionHistory")]
    public string VersionHistoryText => string.Join(", ", VersionHistory.Select(version => version.VersionLabel));
    public IReadOnlyList<DeadlyStreamDownloadOption> AvailableDownloads { get; init; } = Array.Empty<DeadlyStreamDownloadOption>();
}

public sealed class DeadlyStreamVersionInfo
{
    public required string VersionLabel { get; init; }
    public required string ChangelogUrl { get; init; }
    public string? ReleaseDate { get; init; }
    public bool IsCurrentSelection { get; init; }
}

public sealed class DeadlyStreamDownloadOption
{
    public required string FileName { get; init; }
    [JsonIgnore]
    public string DownloadUrl { get; init; } = string.Empty;
    public string? RemoteFileId { get; init; }
}

public sealed class DeadlyStreamDownloadResult
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required HttpStatusCode StatusCode { get; init; }
}
