using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace DeadlyScraper;

public sealed class DeadlyStreamClient
{
    private static readonly Regex DownloadLinkRegex = new(
        "<a[^>]+href=[\"'](?<href>[^\"']+)[\"'][^>]*>\\s*Download this file\\s*</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex JsonDownloadUrlRegex = new(
        "\"downloadUrl\"\\s*:\\s*\"(?<href>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SubmittedDateRegex = new(
        "<strong>\\s*Submitted\\s*</strong>\\s*</span>\\s*<span[^>]*>\\s*<time[^>]+datetime=['\"](?<value>[^'\"]+)['\"]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex PublishedDateRegex = new(
        "<strong>\\s*Published\\s*</strong>\\s*</span>\\s*<span[^>]*>\\s*<time[^>]+datetime=['\"](?<value>[^'\"]+)['\"]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex MetaTitleRegex = new(
        "<meta[^>]+property=[\"']og:title[\"'][^>]+content=[\"'](?<value>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DocumentTitleRegex = new(
        "<title>(?<value>.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CurrentVersionRegex = new(
        "data-role=['\"]versionTitle['\"]>(?<value>.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CurrentVersionReleaseRegex = new(
        "Released\\s*<time[^>]+datetime=['\"](?<value>[^'\"]+)['\"]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ChangelogMenuItemRegex = new(
        "<li[^>]*class=['\"][^'\"]*ipsMenu_item(?<checked>[^'\"]*ipsMenu_itemChecked[^'\"]*)?[^'\"]*['\"][^>]*>\\s*<a\\s+href=['\"](?<href>[^'\"]+)['\"][^>]*title=['\"]See changelog for version (?<label>[^'\"]+)['\"][^>]*>\\s*(?<body>.*?)</a>\\s*</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TimeTagRegex = new(
        "<time[^>]+datetime=['\"](?<value>[^'\"]+)['\"]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AnchorRegex = new(
        "<a[^>]+href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DownloadListItemRegex = new(
        "<li[^>]*class=['\"][^'\"]*ipsDataItem[^'\"]*['\"][^>]*>.*?<span[^>]*class=['\"][^'\"]*ipsType_break[^'\"]*['\"][^>]*>(?<name>.*?)</span>.*?<p[^>]*class=['\"][^'\"]*ipsDataItem_meta[^'\"]*['\"][^>]*>(?<size>.*?)</p>.*?<a[^>]+href=['\"](?<href>[^'\"]+)['\"][^>]*data-action=['\"]download['\"][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HrefRegex = new(
        "href=[\"'](?<href>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FormActionRegex = new(
        "<form[^>]+action=[\"'](?<href>[^\"']+)[\"'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FileNameHintRegex = new(
        "(?<name>[^<>\"]+\\.(?:zip|7z|rar|exe|mod|tga|tpc|tlk))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, string> _htmlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<DeadlyStreamDownloadOption>> _downloadOptionsCache = new(StringComparer.OrdinalIgnoreCase);

    public DeadlyStreamClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(10);

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DeadlyScraper/1.0");
        }

        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        }
    }

    public static bool CanHandle(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsDeadlyStreamHost(uri);
    }

    public async Task<DeadlyStreamFileMetadata> GetMetadataForVersionAsync(string filePageUrl, string versionLabel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionLabel))
        {
            throw new ArgumentException("Version cannot be empty.", nameof(versionLabel));
        }

        var metadata = await GetMetadataAsync(filePageUrl, cancellationToken).ConfigureAwait(false);
        var version = metadata.VersionHistory.FirstOrDefault(candidate =>
            string.Equals(candidate.VersionLabel, versionLabel.Trim(), StringComparison.OrdinalIgnoreCase));
        if (version is null || !Uri.TryCreate(version.ChangelogUrl, UriKind.Absolute, out var changelogUri))
        {
            throw new InvalidOperationException($"Version '{versionLabel}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(metadata.DownloadPageUrl) ||
            !Uri.TryCreate(metadata.DownloadPageUrl, UriKind.Absolute, out var currentDownloadPageUri))
        {
            throw new InvalidOperationException($"Version '{versionLabel}' does not have a download URL.");
        }

        var downloadPageUri = ApplyVersionedDownloadOverride(changelogUri, currentDownloadPageUri);
        var availableDownloads = await GetAvailableDownloadsForPageAsync(downloadPageUri, cancellationToken).ConfigureAwait(false);

        return new DeadlyStreamFileMetadata
        {
            SourceUrl = metadata.SourceUrl,
            DownloadPageUrl = downloadPageUri.ToString(),
            Title = metadata.Title,
            LatestVersion = metadata.LatestVersion,
            SelectedVersion = version.VersionLabel,
            LatestVersionReleaseDate = metadata.LatestVersionReleaseDate,
            SelectedVersionReleaseDate = version.IsCurrentSelection ? metadata.LatestVersionReleaseDate : version.ReleaseDate,
            OriginalUploadDate = metadata.OriginalUploadDate,
            VersionHistory = metadata.VersionHistory,
            AvailableDownloads = availableDownloads
        };
    }

    public async Task<DeadlyStreamFileMetadata> GetMetadataAsync(string filePageUrl, CancellationToken cancellationToken = default)
    {
        var filePageUri = ValidateFilePageUrl(filePageUrl);
        var html = await GetStringAsync(filePageUri, cancellationToken).ConfigureAwait(false);
        var downloadPageUri = ResolveDownloadPageUri(filePageUri, html);
        var inlineDownloads = await GetInlineVersionDownloadsAsync(filePageUri, html, cancellationToken).ConfigureAwait(false);
        var availableDownloads = inlineDownloads.Count > 0
            ? inlineDownloads
            : await GetAvailableDownloadsForPageAsync(downloadPageUri, cancellationToken).ConfigureAwait(false);

        var currentVersionReleaseDate = FirstGroupValue(CurrentVersionReleaseRegex, html, "value");
        var selectedChangelogId = GetQueryParameter(filePageUri, "changelog");
        if (!string.IsNullOrWhiteSpace(selectedChangelogId) && !string.Equals(selectedChangelogId, "0", StringComparison.OrdinalIgnoreCase))
        {
            var basePageUri = new Uri(filePageUri.GetLeftPart(UriPartial.Path));
            var baseHtml = await GetStringAsync(basePageUri, cancellationToken).ConfigureAwait(false);
            currentVersionReleaseDate = FirstGroupValue(CurrentVersionReleaseRegex, baseHtml, "value") ?? currentVersionReleaseDate;
        }

        var submittedDate = FirstGroupValue(SubmittedDateRegex, html, "value");
        var publishedDate = FirstGroupValue(PublishedDateRegex, html, "value");
        if (string.IsNullOrWhiteSpace(publishedDate))
        {
            publishedDate = submittedDate;
        }

        if (string.IsNullOrWhiteSpace(currentVersionReleaseDate))
        {
            currentVersionReleaseDate = publishedDate;
        }

        return new DeadlyStreamFileMetadata
        {
            SourceUrl = filePageUri.GetLeftPart(UriPartial.Path),
            DownloadPageUrl = downloadPageUri.ToString(),
            Title = ExtractTitle(html),
            LatestVersion = ExtractCurrentVersion(html),
            LatestVersionReleaseDate = currentVersionReleaseDate,
            OriginalUploadDate = submittedDate,
            VersionHistory = ExtractVersionHistory(filePageUri, html, currentVersionReleaseDate),
            AvailableDownloads = availableDownloads
        };
    }
    public async Task<IReadOnlyList<DeadlyStreamDownloadResult>> DownloadFilesAsync(DeadlyStreamFileMetadata metadata, string destinationDirectory, string? fileName = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("Download directory cannot be empty.", nameof(destinationDirectory));
        }

        IReadOnlyList<DeadlyStreamDownloadOption> options = metadata.AvailableDownloads;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var selectedOption = options.FirstOrDefault(option =>
                string.Equals(option.FileName, fileName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (selectedOption is null)
            {
                var availableNames = string.Join(", ", options.Select(option => option.FileName));
                throw new InvalidOperationException($"File '{fileName}' was not found. Available files: {availableNames}");
            }

            options = new[] { selectedOption };
        }

        if (options.Count == 0)
        {
            throw new InvalidOperationException("No downloadable files were found for this version.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var results = new List<DeadlyStreamDownloadResult>(options.Count);
        foreach (var option in options)
        {
            if (!Uri.TryCreate(option.DownloadUrl, UriKind.Absolute, out var downloadUri))
            {
                throw new InvalidOperationException($"File '{option.FileName}' does not have a valid download URL.");
            }

            using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (IsHtmlResponse(response))
            {
                throw new InvalidOperationException($"DeadlyStream returned an HTML page instead of '{option.FileName}'.");
            }

            var downloadedFileName = GetSafeFileName(response, downloadUri);
            var requestedOutputPath = Path.Combine(destinationDirectory, downloadedFileName);
            var outputPath = File.Exists(requestedOutputPath) ? GetUniquePath(requestedOutputPath) : requestedOutputPath;
            var temporaryPath = GetUniquePath(outputPath + ".partial");

            try
            {
                using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                using (var output = File.Create(temporaryPath))
                {
                    await CopyToWithProgressAsync(input, output, response.Content.Headers.ContentLength, progress, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, outputPath);
            }
            catch
            {
                File.Delete(temporaryPath);
                throw;
            }

            results.Add(new DeadlyStreamDownloadResult
            {
                FilePath = outputPath,
                FileName = Path.GetFileName(outputPath),
                StatusCode = response.StatusCode
            });
        }

        return results;
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        var cacheKey = uri.ToString();
        if (_htmlCache.TryGetValue(cacheKey, out var cachedHtml))
        {
            return cachedHtml;
        }

        var html = await _httpClient.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        _htmlCache.TryAdd(cacheKey, html);
        return html;
    }

    private static string? ExtractTitle(string html)
    {
        var metaTitle = FirstGroupValue(MetaTitleRegex, html, "value");
        if (!string.IsNullOrWhiteSpace(metaTitle))
        {
            return metaTitle;
        }

        var documentTitle = FirstGroupValue(DocumentTitleRegex, html, "value");
        if (string.IsNullOrWhiteSpace(documentTitle))
        {
            return null;
        }

        documentTitle = WebUtility.HtmlDecode(Regex.Replace(documentTitle, "\\s+", " ")).Trim();
        const string suffix = " - Skins - Deadly Stream";
        if (documentTitle.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return documentTitle[..^suffix.Length].Trim();
        }

        return documentTitle;
    }

    private static string? ExtractCurrentVersion(string html)
    {
        var value = FirstGroupValue(CurrentVersionRegex, html, "value");
        return string.IsNullOrWhiteSpace(value) ? null : SanitizeWhitespace(WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")));
    }
    private static List<DeadlyStreamVersionInfo> ExtractVersionHistory(Uri baseUri, string html, string? currentVersionReleaseDate)
    {
        var versions = new List<DeadlyStreamVersionInfo>();
        foreach (Match match in ChangelogMenuItemRegex.Matches(html ?? string.Empty))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUri, href, out var changelogUri))
            {
                continue;
            }

            var body = match.Groups["body"].Value;
            var releaseDate = FirstGroupValue(TimeTagRegex, body, "value");
            if (string.IsNullOrWhiteSpace(releaseDate) &&
                string.Equals(GetQueryParameter(changelogUri, "changelog"), "0", StringComparison.OrdinalIgnoreCase))
            {
                releaseDate = currentVersionReleaseDate;
            }
            var label = SanitizeWhitespace(WebUtility.HtmlDecode(match.Groups["label"].Value));
            var isCurrent = match.Value.Contains("ipsMenu_itemChecked", StringComparison.OrdinalIgnoreCase);

            if (versions.Any(existing => existing.ChangelogUrl.Equals(changelogUri.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            versions.Add(new DeadlyStreamVersionInfo
            {
                VersionLabel = label,
                ChangelogUrl = changelogUri.ToString(),
                ReleaseDate = releaseDate,
                IsCurrentSelection = isCurrent
            });
        }

        return versions;
    }

    private async Task<IReadOnlyList<DeadlyStreamDownloadOption>> GetAvailableDownloadsForPageAsync(Uri downloadPageUri, CancellationToken cancellationToken)
    {
        var cacheKey = downloadPageUri.ToString();
        if (_downloadOptionsCache.TryGetValue(cacheKey, out var cachedOptions))
        {
            return cachedOptions;
        }

        var resolvedOptions = await GetAvailableDownloadsForPageAsync(downloadPageUri, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
        _downloadOptionsCache.TryAdd(cacheKey, resolvedOptions);
        return resolvedOptions;
    }

    private async Task<IReadOnlyList<DeadlyStreamDownloadOption>> GetAvailableDownloadsForPageAsync(Uri downloadPageUri, HashSet<string> visitedUrls, CancellationToken cancellationToken)
    {
        if (!visitedUrls.Add(downloadPageUri.ToString()))
        {
            return Array.Empty<DeadlyStreamDownloadOption>();
        }

        using var response = await GetMetadataResponseAsync(downloadPageUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (!IsHtmlResponse(response))
        {
            return new[] { CreateDownloadOptionFromResponse(response, downloadPageUri) };
        }

        var downloadHtml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var options = ExtractDownloadOptions(downloadPageUri, downloadHtml);
        if (options.Count > 0)
        {
            return options;
        }

        foreach (var candidateUri in ExtractDownloadCandidates(downloadPageUri, downloadHtml))
        {
            if (UrisEqualIgnoringFragment(candidateUri, downloadPageUri) || visitedUrls.Contains(candidateUri.ToString()))
            {
                continue;
            }

            var candidateOptions = await GetAvailableDownloadsForPageAsync(candidateUri, visitedUrls, cancellationToken).ConfigureAwait(false);
            if (candidateOptions.Count > 0)
            {
                return candidateOptions;
            }
        }

        return Array.Empty<DeadlyStreamDownloadOption>();
    }

    private static DeadlyStreamDownloadOption CreateDownloadOptionFromResponse(HttpResponseMessage response, Uri fallbackUri)
    {
        var downloadUri = response.RequestMessage?.RequestUri ?? fallbackUri;
        var fileName = GetRemoteFileName(response, fallbackUri);
        var remoteFileId = GetQueryParameter(downloadUri, "r");
        return new DeadlyStreamDownloadOption
        {
            FileName = fileName,
            DownloadUrl = downloadUri.ToString(),
            RemoteFileId = remoteFileId
        };
    }

    private async Task<HttpResponseMessage> GetMetadataResponseAsync(Uri downloadUri, CancellationToken cancellationToken)
    {
        return await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<Uri> ExtractDownloadCandidates(Uri baseUri, string html)
    {
        var candidates = new List<Uri>();
        AddCandidateFromMatch(candidates, baseUri, DownloadLinkRegex.Match(html ?? string.Empty), "href");
        AddCandidateFromMatch(candidates, baseUri, JsonDownloadUrlRegex.Match(html ?? string.Empty), "href", decodeJsonSlashes: true);

        foreach (Match match in AnchorRegex.Matches(html ?? string.Empty))
        {
            AddCandidateFromMatch(candidates, baseUri, match, "href");
        }

        foreach (Match match in FormActionRegex.Matches(html ?? string.Empty))
        {
            AddCandidateFromMatch(candidates, baseUri, match, "href");
        }

        return candidates
            .GroupBy(uri => uri.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void AddCandidateFromMatch(List<Uri> candidates, Uri baseUri, Match match, string groupName, bool decodeJsonSlashes = false)
    {
        if (!match.Success)
        {
            return;
        }

        var href = WebUtility.HtmlDecode(match.Groups[groupName].Value);
        if (decodeJsonSlashes)
        {
            href = href.Replace("\\/", "/");
        }

        if (Uri.TryCreate(baseUri, href, out var uri) && IsPlausibleDownloadUri(baseUri, uri))
        {
            candidates.Add(uri);
        }
    }

    private async Task<IReadOnlyList<DeadlyStreamDownloadOption>> GetInlineVersionDownloadsAsync(Uri changelogUri, string html, CancellationToken cancellationToken)
    {
        var candidates = ExtractDownloadCandidates(changelogUri, html ?? string.Empty);
        var changelogId = GetQueryParameter(changelogUri, "changelog");
        if (!string.IsNullOrWhiteSpace(changelogId) && !string.Equals(changelogId, "0", StringComparison.OrdinalIgnoreCase))
        {
            var matchingVersionCandidates = candidates
                .Where(candidate => string.Equals(GetQueryParameter(candidate, "version"), changelogId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchingVersionCandidates.Count > 0)
            {
                candidates = matchingVersionCandidates;
            }
        }

        var downloads = new List<DeadlyStreamDownloadOption>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var downloadUri in candidates)
        {
            if (!seenUrls.Add(downloadUri.ToString()))
            {
                continue;
            }

            using var response = await GetMetadataResponseAsync(downloadUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (!IsHtmlResponse(response))
            {
                var option = CreateDownloadOptionFromResponse(response, downloadUri);
                if (string.IsNullOrWhiteSpace(option.RemoteFileId))
                {
                    return new[] { option };
                }

                downloads.Add(option);
                continue;
            }

            var downloadHtml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DeadlyStreamDownloadOption> resolvedDownloads = ExtractDownloadOptions(downloadUri, downloadHtml);
            if (resolvedDownloads.Count > 0)
            {
                _downloadOptionsCache.TryAdd(downloadUri.ToString(), resolvedDownloads);
            }
            else
            {
                resolvedDownloads = await GetAvailableDownloadsForPageAsync(downloadUri, cancellationToken).ConfigureAwait(false);
            }

            if (resolvedDownloads.Count > 0)
            {
                downloads.AddRange(resolvedDownloads);
            }
        }

        return DeduplicateOptions(downloads);
    }

    private static bool UrisEqualIgnoringFragment(Uri left, Uri right)
    {
        return Uri.Compare(
            left,
            right,
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer()
        };

        return new HttpClient(handler, disposeHandler: true);
    }

    private static async Task CopyToWithProgressAsync(Stream input, Stream output, long? contentLength, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long totalRead = 0;
        var lastProgress = -1;

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;

            if (progress is null || !contentLength.HasValue || contentLength.Value <= 0)
            {
                continue;
            }

            var percent = (int)Math.Min(100, (totalRead * 100L) / contentLength.Value);
            if (percent != lastProgress)
            {
                lastProgress = percent;
                progress.Report(percent);
            }
        }
    }

    private static string? FirstGroupValue(Regex regex, string input, string groupName)
    {
        var match = regex.Match(input ?? string.Empty);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[groupName].Value) : null;
    }

    private static List<DeadlyStreamDownloadOption> ExtractDownloadOptions(Uri baseUri, string html)
    {
        var options = new List<DeadlyStreamDownloadOption>();
        var htmlText = html ?? string.Empty;

        foreach (Match match in DownloadListItemRegex.Matches(htmlText))
        {
            var fileName = DecodeFileName(match.Groups["name"].Value);
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUri, href, out var downloadUri) || !IsPlausibleDownloadUri(baseUri, downloadUri))
            {
                continue;
            }

            options.Add(new DeadlyStreamDownloadOption
            {
                FileName = fileName,
                DownloadUrl = downloadUri.ToString(),
                RemoteFileId = GetQueryParameter(downloadUri, "r")
            });
        }

        if (options.Count > 0)
        {
            return DeduplicateOptions(options);
        }

        foreach (Match match in AnchorRegex.Matches(htmlText))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUri, href, out var downloadUri) || !IsPlausibleDownloadUri(baseUri, downloadUri))
            {
                continue;
            }

            var innerText = Regex.Replace(WebUtility.HtmlDecode(match.Groups["text"].Value), "<[^>]+>", " ");
            var fileName = GetFirstFileName(innerText);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                var contextStart = Math.Max(0, match.Index - 200);
                var contextLength = Math.Min(htmlText.Length - contextStart, match.Length + 400);
                fileName = GetFirstFileName(WebUtility.HtmlDecode(htmlText.Substring(contextStart, contextLength)));
            }

            options.Add(new DeadlyStreamDownloadOption
            {
                FileName = (fileName ?? Path.GetFileName(downloadUri.LocalPath)).Trim(),
                DownloadUrl = downloadUri.ToString(),
                RemoteFileId = GetQueryParameter(downloadUri, "r")
            });
        }

        foreach (Match fileMatch in FileNameHintRegex.Matches(WebUtility.HtmlDecode(htmlText)))
        {
            var fileName = fileMatch.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var contextStart = Math.Max(0, fileMatch.Index - 1200);
            var contextLength = Math.Min(htmlText.Length - contextStart, 2400);
            var contextHtml = htmlText.Substring(contextStart, contextLength);
            foreach (Match hrefMatch in HrefRegex.Matches(contextHtml))
            {
                var href = WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value);
                if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUri, href, out var downloadUri) || !IsPlausibleDownloadUri(baseUri, downloadUri))
                {
                    continue;
                }

                options.Add(new DeadlyStreamDownloadOption
                {
                    FileName = fileName,
                    DownloadUrl = downloadUri.ToString(),
                    RemoteFileId = GetQueryParameter(downloadUri, "r")
                });
            }
        }

        return DeduplicateOptions(options);
    }

    private static bool IsPlausibleDownloadUri(Uri baseUri, Uri candidateUri)
    {
        if (!string.IsNullOrWhiteSpace(candidateUri.Fragment))
        {
            return false;
        }

        if (UrisEqualIgnoringFragment(baseUri, candidateUri))
        {
            return false;
        }

        var query = ParseQueryString(candidateUri.Query);
        return query.ContainsKey("r") ||
               query.ContainsKey("confirm") ||
               query.ContainsKey("version") ||
               string.Equals(query.GetValueOrDefault("do"), "download", StringComparison.OrdinalIgnoreCase) ||
               candidateUri.AbsolutePath.Contains("download", StringComparison.OrdinalIgnoreCase);
    }

    private static List<DeadlyStreamDownloadOption> DeduplicateOptions(IEnumerable<DeadlyStreamDownloadOption> options)
    {
        return options
            .Where(option => !string.IsNullOrWhiteSpace(option.FileName) && !string.IsNullOrWhiteSpace(option.DownloadUrl))
            .GroupBy(option => string.Format(CultureInfo.InvariantCulture, "{0}|{1}", option.FileName, option.DownloadUrl), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string? GetFirstFileName(string text)
    {
        var match = FileNameHintRegex.Match(text ?? string.Empty);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static string DecodeFileName(string value)
    {
        return WebUtility.HtmlDecode(Regex.Replace(value ?? string.Empty, "<[^>]+>", string.Empty));
    }

    private static string SanitizeWhitespace(string value)
    {
        return Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
    }

    private static bool IsHtmlResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return !string.IsNullOrWhiteSpace(mediaType) && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri ResolveDownloadPageUri(Uri filePageUri, string html)
    {
        var match = DownloadLinkRegex.Match(html ?? string.Empty);
        if (match.Success)
        {
            return ApplyVersionedDownloadOverride(filePageUri, new Uri(filePageUri, WebUtility.HtmlDecode(match.Groups["href"].Value)));
        }

        var jsonMatch = JsonDownloadUrlRegex.Match(html ?? string.Empty);
        if (jsonMatch.Success)
        {
            var jsonHref = WebUtility.HtmlDecode(jsonMatch.Groups["href"].Value.Replace("\\/", "/"));
            return ApplyVersionedDownloadOverride(filePageUri, new Uri(filePageUri, jsonHref));
        }

        throw new InvalidOperationException("Could not find the DeadlyStream download link on the file page.");
    }

    private static Uri ApplyVersionedDownloadOverride(Uri filePageUri, Uri downloadPageUri)
    {
        var changelogId = GetQueryParameter(filePageUri, "changelog");
        if (string.IsNullOrWhiteSpace(changelogId) || string.Equals(changelogId, "0", StringComparison.OrdinalIgnoreCase))
        {
            return downloadPageUri;
        }

        if (!string.Equals(GetQueryParameter(downloadPageUri, "do"), "download", StringComparison.OrdinalIgnoreCase))
        {
            return downloadPageUri;
        }

        if (!string.IsNullOrWhiteSpace(GetQueryParameter(downloadPageUri, "version")))
        {
            return downloadPageUri;
        }

        var builder = new UriBuilder(downloadPageUri);
        var query = ParseQueryString(builder.Query);
        query["version"] = changelogId;
        builder.Query = BuildQueryString(query);
        return builder.Uri;
    }

    private static string? GetQueryParameter(Uri uri, string name)
    {
        var query = ParseQueryString(uri.Query);
        return query.TryGetValue(name, out var value) ? value : null;
    }

    private static Dictionary<string, string> ParseQueryString(string queryText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = (queryText ?? string.Empty).TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return result;
        }

        foreach (var pair in trimmed.Split('&'))
        {
            if (string.IsNullOrWhiteSpace(pair))
            {
                continue;
            }

            var pieces = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0]);
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string BuildQueryString(Dictionary<string, string> query)
    {
        return string.Join("&", query.Select(kvp => string.Format(CultureInfo.InvariantCulture, "{0}={1}", Uri.EscapeDataString(kvp.Key), Uri.EscapeDataString(kvp.Value ?? string.Empty))));
    }

    private static Uri ValidateFilePageUrl(string filePageUrl)
    {
        if (string.IsNullOrWhiteSpace(filePageUrl))
        {
            throw new ArgumentException("DeadlyStream URL cannot be empty.", nameof(filePageUrl));
        }

        var uri = new Uri(filePageUrl);
        if (!IsDeadlyStreamHost(uri))
        {
            throw new InvalidOperationException("URL is not a deadlystream.com file page.");
        }

        return uri;
    }

    private static bool IsDeadlyStreamHost(Uri uri)
    {
        return uri.Host.Equals("deadlystream.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".deadlystream.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRemoteFileName(HttpResponseMessage response, Uri fallbackUri)
    {
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = response.Content.Headers.ContentDisposition?.FileName;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fallbackUri.Segments.LastOrDefault();
        }

        if (string.IsNullOrWhiteSpace(fileName) || fileName.EndsWith('/'))
        {
            fileName = "deadlystream-download.bin";
        }

        fileName = fileName.Trim().Trim('"');
        return fileName;
    }

    private static string GetSafeFileName(HttpResponseMessage response, Uri fallbackUri)
    {
        var fileName = GetRemoteFileName(response, fallbackUri);
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return fileName;
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? throw new IOException(string.Format(CultureInfo.InvariantCulture, "Could not determine directory for {0}", path));
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, "{0} ({1}){2}", name, index, extension));
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException(string.Format(CultureInfo.InvariantCulture, "Could not create a unique file path for {0}", path));
    }
}
