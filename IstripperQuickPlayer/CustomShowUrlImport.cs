using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IStripperQuickPlayer;

internal sealed record CustomShowUrlDownload(
    string WorkingFolder, string VideoPath, string Title, string SourceUrl);

internal sealed record XVideosAdaptiveSource(string HlsUrl, string Title);

internal static partial class YtDlpSupport
{
    const string DownloadUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string ChecksumsUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    const string DenoDownloadUrl =
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    const string DenoChecksumUrl = DenoDownloadUrl + ".sha256sum";

    internal static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "yt-dlp");
    internal static string ExecutablePath => Path.Combine(Root, "yt-dlp.exe");
    internal static string DenoPath => Path.Combine(Root, "deno.exe");
    internal static string VersionPath => Path.Combine(Root, "version.txt");
    internal static string DenoVersionPath => Path.Combine(Root, "deno-version.txt");
    internal static bool IsInstalled => File.Exists(ExecutablePath) &&
        new FileInfo(ExecutablePath).Length > 1_000_000 &&
        File.Exists(DenoPath) && new FileInfo(DenoPath).Length > 1_000_000;

    [GeneratedRegex(@"(?im)^([0-9a-f]{64})\s+\*?yt-dlp\.exe\s*$")]
    private static partial Regex ExecutableChecksum();

    [GeneratedRegex(@"^\[download\]\s+\d+(?:\.\d+)?%",
        RegexOptions.CultureInvariant)]
    private static partial Regex LiveDownloadProgress();

    internal static bool TryNormalizeUrl(string? value, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        normalized = uri.AbsoluteUri;
        return true;
    }

    internal static async Task InstallAsync(IProgress<string>? progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(Root);
        string staging = Path.Combine(Root,
            ".download-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "IStripperQuickPlayer/1.0 (+https://github.com/KittyPingu/IStripperQuickPlayer)");
            Exception? lastFailure = null;
            bool ytDlpReady = false;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report("Resolving the latest official yt-dlp release...");
                    string sums = await client.GetStringAsync(ChecksumsUrl, token);
                    string expected = ParseExecutableChecksum(sums);
                    if (File.Exists(ExecutablePath) &&
                        string.Equals(HashFile(ExecutablePath), expected,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string installedVersion = await ReadVersionAsync(
                            ExecutablePath, token);
                        WriteTextAtomic(VersionPath, installedVersion);
                        progress?.Report($"yt-dlp {installedVersion} is already installed.");
                        ytDlpReady = true;
                        break;
                    }

                    progress?.Report("Downloading yt-dlp.exe...");
                    await DownloadFileAsync(client, DownloadUrl, staging, token);
                    string actual = HashFile(staging);
                    if (!string.Equals(actual, expected,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "The yt-dlp download did not match the official SHA-256 checksum.");
                    string version = await ReadVersionAsync(staging, token);
                    File.Move(staging, ExecutablePath, true);
                    WriteTextAtomic(VersionPath, version);
                    progress?.Report($"yt-dlp {version} installed successfully.");
                    ytDlpReady = true;
                    break;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    lastFailure = error;
                    try { File.Delete(staging); } catch { }
                    if (attempt < 2)
                        progress?.Report("The release changed or the download failed; retrying once...");
                }
            }
            if (!ytDlpReady)
                throw new InvalidOperationException(
                    "yt-dlp setup failed after two attempts.", lastFailure);
            await InstallDenoAsync(client, progress, token);
        }
        finally
        {
            try { File.Delete(staging); } catch { }
        }
    }

    static async Task InstallDenoAsync(HttpClient client,
        IProgress<string>? progress, CancellationToken token)
    {
        string archive = Path.Combine(Root,
            ".deno-" + Guid.NewGuid().ToString("N") + ".zip");
        string extracted = Path.Combine(Root,
            ".deno-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report("Resolving the latest official Deno runtime...");
            string checksumText = await client.GetStringAsync(DenoChecksumUrl, token);
            string expected = ParseDenoChecksum(checksumText);
            progress?.Report("Downloading the Deno JavaScript runtime...");
            await DownloadFileAsync(client, DenoDownloadUrl, archive, token);
            string actual = HashFile(archive);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The Deno download did not match the official SHA-256 checksum.");
            Directory.CreateDirectory(extracted);
            ZipFile.ExtractToDirectory(archive, extracted);
            string candidate = Path.Combine(extracted, "deno.exe");
            if (!File.Exists(candidate))
                throw new InvalidDataException("The Deno archive did not contain deno.exe.");
            string version = await ReadDenoVersionAsync(candidate, token);
            File.Move(candidate, DenoPath, true);
            WriteTextAtomic(DenoVersionPath, version);
            progress?.Report($"Deno {version} installed successfully.");
        }
        finally
        {
            try { File.Delete(archive); } catch { }
            try { Directory.Delete(extracted, true); } catch { }
        }
    }

    internal static async Task<CustomShowUrlDownload> DownloadAsync(
        CustomShowStore store, string url, string? browserCookies,
        IProgress<string>? progress, CancellationToken token)
        => await DownloadAsync(store, url, browserCookies, null, progress, token);

    internal static async Task<CustomShowUrlDownload> DownloadAsync(
        CustomShowStore store, string url, string? browserCookies,
        string? cookieFile, IProgress<string>? progress, CancellationToken token)
    {
        if (!TryNormalizeUrl(url, out string normalized))
            throw new InvalidDataException("Enter a valid HTTP or HTTPS video page URL.");
        if (!IsInstalled)
            throw new FileNotFoundException(
                "yt-dlp is not installed. Use Install / Update yt-dlp first.",
                ExecutablePath);
        CleanupStaleDownloads(store);
        string root = Path.Combine(store.Root, ".url-downloads");
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string? transientCookieFile = null;
        Directory.CreateDirectory(folder);
        try
        {
            string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (!File.Exists(ffmpeg))
                throw new FileNotFoundException(
                    "QuickPlayer's FFmpeg executable is missing.", ffmpeg);
            if (!string.IsNullOrWhiteSpace(cookieFile))
                transientCookieFile = CreateTransientCookieFile(cookieFile);
            (int exitCode, List<string> errorTail) = await RunDownloadProcessAsync(
                normalized, folder, Path.GetDirectoryName(ffmpeg)!, browserCookies,
                transientCookieFile, false, null, progress, token);
            XVideosAdaptiveSource? adaptiveSource = null;
            if (exitCode != 0 && ShouldRetryWithGenericExtractor(errorTail))
            {
                adaptiveSource = await TryResolveXVideosAdaptiveSourceAsync(
                    normalized, token);
                if (adaptiveSource != null)
                {
                    progress?.Report("The XVideos extractor found no formats; " +
                        "using the player's best adaptive stream...");
                    (exitCode, errorTail) = await RunDownloadProcessAsync(
                        adaptiveSource.HlsUrl, folder,
                        Path.GetDirectoryName(ffmpeg)!, browserCookies,
                        transientCookieFile, false, normalized, progress, token);
                }
                if (exitCode != 0)
                {
                    adaptiveSource = null;
                    progress?.Report("The site extractor found no formats; retrying " +
                        "with yt-dlp's generic page extractor...");
                    (exitCode, errorTail) = await RunDownloadProcessAsync(normalized,
                        folder, Path.GetDirectoryName(ffmpeg)!, browserCookies,
                        transientCookieFile, true, null, progress, token);
                }
            }
            if (exitCode != 0)
                throw new InvalidOperationException(FormatDownloadError(errorTail,
                    browserCookies, transientCookieFile != null, exitCode));

            string video = FindDownloadedVideo(folder);
            string title = adaptiveSource?.Title ?? ReadTitle(folder);
            string sourceUrl = adaptiveSource != null ? normalized :
                ReadCanonicalUrl(folder) ?? normalized;
            progress?.Report($"Downloaded {Path.GetFileName(video)}");
            return new CustomShowUrlDownload(folder, video, title, sourceUrl);
        }
        catch
        {
            DeleteWorkingFolder(store, folder);
            throw;
        }
        finally
        {
            if (transientCookieFile != null)
                try { File.Delete(transientCookieFile); } catch { }
        }
    }

    static async Task<(int ExitCode, List<string> ErrorTail)>
        RunDownloadProcessAsync(string normalizedUrl, string outputFolder,
            string ffmpegFolder, string? browserCookies, string? cookieFile,
            bool forceGenericExtractor, string? referer,
            IProgress<string>? progress,
            CancellationToken token)
    {
        ProcessStartInfo start = new(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = outputFolder,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in DownloadArguments(normalizedUrl, outputFolder,
                     ffmpegFolder, browserCookies, cookieFile,
                     forceGenericExtractor, referer))
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("yt-dlp could not be started.");
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        List<string> errorTail = [];
        Task output = PumpAsync(process.StandardOutput, progress, null, token);
        Task errors = PumpAsync(process.StandardError, progress, errorTail, token);
        await process.WaitForExitAsync(token);
        await Task.WhenAll(output, errors);
        return (process.ExitCode, errorTail);
    }

    internal static async Task<XVideosAdaptiveSource?>
        TryResolveXVideosAdaptiveSourceAsync(string pageUrl,
            CancellationToken token)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? page) ||
            !(page.Host.Equals("xvideos.com", StringComparison.OrdinalIgnoreCase) ||
              page.Host.EndsWith(".xvideos.com", StringComparison.OrdinalIgnoreCase)))
            return null;
        try
        {
            using HttpClient client = new(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            }) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 Chrome/144.0 Safari/537.36");
            string html = await client.GetStringAsync(page, token);
            Match id = Regex.Match(html,
                @"setEncodedIdVideo\('(?<value>[a-z0-9]+)'\)",
                RegexOptions.IgnoreCase);
            Match cdn = Regex.Match(html,
                @"setIdCdnHLS\('?(?<value>\d+)'?\)",
                RegexOptions.IgnoreCase);
            Match viewData = Regex.Match(html,
                @"setViewData\('(?<value>[^']+)'\)");
            Match title = Regex.Match(html,
                @"setVideoTitle\('(?<value>(?:\\.|[^'])*)'\)");
            if (!id.Success || !cdn.Success || !viewData.Success)
                return null;

            Uri endpoint = new($"https://www.xvideos.com/html5player/getvideo/" +
                $"{id.Groups["value"].Value}/{cdn.Groups["value"].Value}");
            using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
            request.Headers.Referrer = page;
            request.Headers.TryAddWithoutValidation("X-View-Data",
                viewData.Groups["value"].Value);
            using HttpResponseMessage response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using JsonDocument data = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(token));
            if (!data.RootElement.TryGetProperty("hls", out JsonElement hls) ||
                !Uri.TryCreate(hls.GetString(), UriKind.Absolute, out Uri? hlsUri) ||
                hlsUri.Scheme != Uri.UriSchemeHttps ||
                !hlsUri.Host.EndsWith(".xvideos-cdn.com",
                    StringComparison.OrdinalIgnoreCase))
                return null;
            string resolvedTitle = title.Success ? WebUtility.HtmlDecode(
                title.Groups["value"].Value.Replace("\\'", "'")) : "Imported Video";
            resolvedTitle = resolvedTitle.Trim();
            if (resolvedTitle.Length == 0) resolvedTitle = "Imported Video";
            if (resolvedTitle.Length > 240) resolvedTitle = resolvedTitle[..240];
            return new XVideosAdaptiveSource(hlsUri.AbsoluteUri, resolvedTitle);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    internal static void DeleteWorkingFolder(CustomShowStore store, string folder)
    {
        try
        {
            string root = Path.GetFullPath(Path.Combine(store.Root,
                ".url-downloads"));
            string candidate = Path.GetFullPath(folder);
            if (candidate.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                Directory.Delete(candidate, true);
        }
        catch { }
    }

    static void CleanupStaleDownloads(CustomShowStore store)
    {
        string root = Path.Combine(store.Root, ".url-downloads");
        if (!Directory.Exists(root)) return;
        foreach (string folder in Directory.EnumerateDirectories(root))
            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) <
                    DateTime.UtcNow.AddDays(-1))
                    DeleteWorkingFolder(store, folder);
            }
            catch { }
    }

    internal static string[] DownloadArguments(string normalizedUrl,
        string outputFolder, string ffmpegFolder, string? browserCookies,
        string? cookieFile = null, bool forceGenericExtractor = false,
        string? referer = null)
    {
        List<string> arguments =
        [
            "--ignore-config", "--no-playlist", "--newline",
            "--write-info-json", "--no-write-playlist-metafiles",
            "--no-write-comments", "--windows-filenames",
            "--ffmpeg-location", ffmpegFolder,
            "--js-runtimes", "deno:" + DenoPath,
            "--format", "bv*+ba/b",
            "--merge-output-format", "mkv",
            "--output", Path.Combine(outputFolder, "source.%(ext)s")
        ];
        if (!string.IsNullOrWhiteSpace(cookieFile))
        {
            arguments.Add("--cookies");
            arguments.Add(Path.GetFullPath(cookieFile));
        }
        else if (BrowserCookieValue(browserCookies) is string cookies)
        {
            arguments.Add("--cookies-from-browser");
            arguments.Add(cookies);
        }
        if (forceGenericExtractor)
            arguments.Add("--force-generic-extractor");
        if (!string.IsNullOrWhiteSpace(referer))
        {
            arguments.Add("--referer");
            arguments.Add(referer);
        }
        arguments.Add("--");
        arguments.Add(normalizedUrl);
        return [.. arguments];
    }

    internal static bool ShouldRetryWithGenericExtractor(
        IEnumerable<string> errorLines) => errorLines.Any(line =>
            line.Contains("No video formats found", StringComparison.OrdinalIgnoreCase));

    static string CreateTransientCookieFile(string sourcePath)
    {
        string source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("The selected cookie file no longer exists.",
                source);
        using (StreamReader reader = new(source, Encoding.UTF8, true))
        {
            string firstLine = (reader.ReadLine() ?? "").TrimStart('\uFEFF').Trim();
            if (firstLine is not ("# HTTP Cookie File" or
                    "# Netscape HTTP Cookie File"))
                throw new InvalidDataException(
                    "The cookie file must be in Netscape format (its first line must be " +
                    "'# HTTP Cookie File' or '# Netscape HTTP Cookie File').");
        }
        string temporaryRoot = Path.Combine(Path.GetTempPath(),
            "IStripperQuickPlayer", "cookies");
        Directory.CreateDirectory(temporaryRoot);
        foreach (string stale in Directory.EnumerateFiles(temporaryRoot, "*.txt"))
            try
            {
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1))
                    File.Delete(stale);
            }
            catch { }
        string destination = Path.Combine(temporaryRoot,
            Guid.NewGuid().ToString("N") + ".txt");
        File.Copy(source, destination, false);
        File.SetAttributes(destination, FileAttributes.Hidden | FileAttributes.Temporary);
        return destination;
    }

    internal static string FormatDownloadError(IReadOnlyList<string> lines,
        string? browserCookies, bool usedCookieFile, int exitCode)
    {
        string combined = string.Join("\n", lines);
        if (BrowserCookieValue(browserCookies) is string &&
            combined.Contains("Could not copy", StringComparison.OrdinalIgnoreCase) &&
            combined.Contains("cookie database", StringComparison.OrdinalIgnoreCase))
            return $"{browserCookies}'s cookie database is in use. Fully close " +
                $"{browserCookies}, including its background processes, then retry; " +
                "or choose 'Cookie file (Netscape format)' instead.";
        if (BrowserCookieValue(browserCookies) is string &&
            combined.Contains("Failed to decrypt with DPAPI",
                StringComparison.OrdinalIgnoreCase))
            return $"{browserCookies}'s cookies could not be decrypted on this " +
                "Windows system. Retry with 'None (public URL)' if the page is " +
                "public, use a signed-in Firefox profile, or choose 'Cookie file " +
                "(Netscape format)'.";
        if (!usedCookieFile && BrowserCookieValue(browserCookies) == null &&
            (combined.Contains("confirm your age", StringComparison.OrdinalIgnoreCase) ||
             combined.Contains("requires authentication", StringComparison.OrdinalIgnoreCase)))
            return "This video requires an authenticated session. Choose a signed-in " +
                "browser (fully close Chrome, Edge, or Brave first) or select a " +
                "Netscape-format cookie file, then retry.";
        string[] distinct = lines.Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal).TakeLast(8).ToArray();
        return distinct.Length == 0 ? $"yt-dlp failed with exit code {exitCode}." :
            string.Join(Environment.NewLine, distinct);
    }

    static string? BrowserCookieValue(string? value) => value switch
    {
        "Chrome" => "chrome",
        "Edge" => "edge",
        "Firefox" => "firefox",
        "Brave" => "brave",
        _ => null
    };

    static async Task PumpAsync(StreamReader reader, IProgress<string>? progress,
        List<string>? tail, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is string line)
        {
            string clean = line.Trim();
            if (clean.Length == 0) continue;
            progress?.Report(clean);
            if (tail != null)
            {
                tail.Add(clean);
                if (tail.Count > 40) tail.RemoveAt(0);
            }
        }
    }

    internal static bool IsLiveDownloadProgressLine(string value) =>
        LiveDownloadProgress().IsMatch(value);

    static string FindDownloadedVideo(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder)
                     .Where(path => RealtimeFolderImporter.SupportedExtensions
                         .Contains(Path.GetExtension(path)))
                     .OrderByDescending(path => new FileInfo(path).Length))
            try
            {
                using FfmpegCpuDecoder decoder = new(file, fastDecode: true);
                if (decoder.Width > 0 && decoder.Height > 0 && decoder.Duration > 0)
                    return file;
            }
            catch { }
        throw new InvalidDataException(
            "yt-dlp completed but did not produce a supported playable video.");
    }

    static string ReadTitle(string folder)
    {
        try
        {
            using JsonDocument info = ReadInfo(folder);
            if (info.RootElement.TryGetProperty("title", out JsonElement title))
            {
                string value = title.GetString()?.Trim() ?? "";
                if (value.Length > 0)
                    return value.Length <= 240 ? value : value[..240];
            }
        }
        catch { }
        return "Imported Video";
    }

    static string? ReadCanonicalUrl(string folder)
    {
        try
        {
            using JsonDocument info = ReadInfo(folder);
            foreach (string property in new[] { "webpage_url", "original_url" })
                if (info.RootElement.TryGetProperty(property,
                        out JsonElement value) &&
                    TryNormalizeUrl(value.GetString(), out string normalized))
                    return normalized;
        }
        catch { }
        return null;
    }

    static JsonDocument ReadInfo(string folder)
    {
        string path = Directory.EnumerateFiles(folder, "*.info.json")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? throw new FileNotFoundException(
                "yt-dlp metadata was not created.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    static string ParseExecutableChecksum(string sums)
    {
        Match match = ExecutableChecksum().Match(sums);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() :
            throw new InvalidDataException(
                "The official checksum list does not contain yt-dlp.exe.");
    }

    static string ParseDenoChecksum(string value)
    {
        Match match = Regex.Match(value, @"(?i)\b[0-9a-f]{64}\b");
        return match.Success ? match.Value.ToUpperInvariant() :
            throw new InvalidDataException(
                "The official Deno checksum file is invalid.");
    }

    static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    static async Task DownloadFileAsync(HttpClient client, string url,
        string destination, CancellationToken token)
    {
        using HttpResponseMessage response = await client.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(token);
        await using FileStream output = new(destination, FileMode.Create,
            FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(output, token);
    }

    static async Task<string> ReadVersionAsync(string executable,
        CancellationToken token)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--ignore-config");
        start.ArgumentList.Add("--version");
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("The downloaded yt-dlp could not start.");
        string output = await process.StandardOutput.ReadToEndAsync(token);
        string error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        string version = output.Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("The downloaded yt-dlp failed its test run: " +
                error.Trim());
        return version;
    }

    static async Task<string> ReadDenoVersionAsync(string executable,
        CancellationToken token)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--version");
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("The downloaded Deno runtime could not start.");
        string output = await process.StandardOutput.ReadToEndAsync(token);
        string error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        string firstLine = output.Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        Match version = Regex.Match(firstLine, @"^deno\s+(\d+\.\d+\.\d+)",
            RegexOptions.IgnoreCase);
        if (process.ExitCode != 0 || !version.Success ||
            !System.Version.TryParse(version.Groups[1].Value, out Version? parsed) ||
            parsed < new Version(2, 3, 0))
            throw new InvalidDataException(
                "The downloaded Deno runtime failed its test run: " + error.Trim());
        return version.Groups[1].Value;
    }

    static void WriteTextAtomic(string path, string value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, value + Environment.NewLine,
            new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    internal static bool VerifyContracts()
    {
        string root = Path.Combine(Path.GetTempPath(), "iqp-url-import");
        bool valid = TryNormalizeUrl(" https://example.com/watch?v=1 ",
            out string normalized) && normalized ==
                "https://example.com/watch?v=1";
        bool invalid = !TryNormalizeUrl("file:///c:/video.mp4", out _) &&
            !TryNormalizeUrl("https://user:secret@example.com/video", out _);
        string checksum = new string('a', 64);
        bool parsed = ParseExecutableChecksum(
            checksum + "  yt-dlp.exe\n" + new string('b', 64) +
            "  yt-dlp_arm64.exe") == checksum.ToUpperInvariant() &&
            ParseDenoChecksum("Hash : " + checksum) == checksum.ToUpperInvariant();
        string[] arguments = DownloadArguments(normalized, root, root, "Edge");
        bool safeArguments = arguments[^2] == "--" &&
            arguments[^1] == normalized &&
            arguments.Contains("--no-playlist") &&
            arguments.Contains("--cookies-from-browser") &&
            arguments.Contains("edge") &&
            arguments.Contains("--js-runtimes") &&
            arguments.Contains("deno:" + DenoPath) &&
            arguments.Contains(Path.Combine(root, "source.%(ext)s"));
        string cookiePath = Path.Combine(root, "cookies.txt");
        string[] fileArguments = DownloadArguments(normalized, root, root,
            null, cookiePath);
        bool cookieArguments = fileArguments.Contains("--cookies") &&
            fileArguments.Contains(Path.GetFullPath(cookiePath)) &&
            !fileArguments.Contains("--cookies-from-browser");
        string[] genericArguments = DownloadArguments(normalized, root, root,
            "Firefox", null, true);
        bool genericFallback = genericArguments.Contains(
                "--force-generic-extractor") &&
            ShouldRetryWithGenericExtractor(
                ["ERROR: No video formats found!; please report this issue"]) &&
            !ShouldRetryWithGenericExtractor(["ERROR: Unsupported URL"]);
        string[] adaptiveArguments = DownloadArguments(normalized, root, root,
            null, null, false, normalized);
        bool adaptiveFallback = adaptiveArguments.Contains("--referer") &&
            adaptiveArguments.Count(value => value == normalized) == 2 &&
            TryResolveXVideosAdaptiveSourceAsync(normalized,
                CancellationToken.None).GetAwaiter().GetResult() == null;
        bool friendlyLock = FormatDownloadError(
            ["ERROR: Could not copy Chrome cookie database."], "Chrome", false, 1)
            .Contains("Fully close Chrome", StringComparison.Ordinal);
        bool friendlyAuthentication = FormatDownloadError(
            ["ERROR: Sign in to confirm your age."], null, false, 1)
            .Contains("authenticated session", StringComparison.Ordinal);
        bool friendlyDpapi = FormatDownloadError(
            ["ERROR: Failed to decrypt with DPAPI."], "Chrome", false, 1)
            .Contains("None (public URL)", StringComparison.Ordinal);
        bool progressLines = IsLiveDownloadProgressLine(
                "[download] 84.7% of ~ 76.20MiB at 3.44MiB/s ETA 00:04") &&
            !IsLiveDownloadProgressLine("[download] Destination: source.mp4") &&
            !IsLiveDownloadProgressLine("[generic] Extracting information");
        return valid && invalid && parsed && safeArguments && cookieArguments &&
            genericFallback && friendlyLock && friendlyAuthentication &&
            friendlyDpapi && adaptiveFallback && progressLines;
    }
}

internal sealed class YtDlpSetupForm : Form
{
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical
    };
    readonly Button close = new() { Dock = DockStyle.Bottom, Height = 38,
        Text = "Cancel" };
    readonly CancellationTokenSource cancellation = new();
    bool complete;

    internal YtDlpSetupForm()
    {
        Text = "Install / Update URL Downloader (yt-dlp + Deno)";
        ClientSize = new Size(720, 300);
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(output);
        Controls.Add(close);
        close.Click += (_, _) =>
        {
            if (!complete)
            {
                close.Enabled = false;
                close.Text = "Cancelling...";
                cancellation.Cancel();
            }
            else Close();
        };
        FormClosing += (_, eventArgs) =>
        {
            if (complete) return;
            eventArgs.Cancel = true;
            close.Enabled = false;
            close.Text = "Cancelling...";
            cancellation.Cancel();
        };
        Shown += async (_, _) => await InstallAsync();
        AppTheme.Apply(this);
    }

    async Task InstallAsync()
    {
        DialogResult completion = DialogResult.None;
        try
        {
            await YtDlpSupport.InstallAsync(new Progress<string>(Append),
                cancellation.Token);
            completion = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            completion = DialogResult.Cancel;
        }
        catch (Exception error)
        {
            Append("Setup failed: " + error.Message);
        }
        finally
        {
            complete = true;
            close.Enabled = true;
            close.Text = "Close";
            if (completion != DialogResult.None)
                DialogResult = completion;
        }
    }

    void Append(string message) => output.AppendText(message +
        Environment.NewLine);
}

internal sealed class CustomShowUrlImportForm : Form
{
    const string CookieFileOption = "Cookie file (Netscape format)";
    readonly CustomShowStore store;
    readonly TextBox url = new() { Dock = DockStyle.Fill };
    readonly ComboBox cookies = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 330 };
    readonly TextBox cookieFile = new() { Dock = DockStyle.Fill, ReadOnly = true,
        Enabled = false };
    readonly Button browseCookieFile = new() { Text = "Browse...", AutoSize = true,
        Enabled = false };
    readonly Label cookieFileLabel = new() { Text = "Cookie file", AutoSize = true,
        Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3),
        Enabled = false };
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical
    };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill,
        Style = ProgressBarStyle.Marquee, Visible = false };
    readonly Button download = new() { Text = "Download and Create...",
        AutoSize = true };
    readonly Button setup = new() { Text = "Install / Update yt-dlp...",
        AutoSize = true };
    readonly Button cancel = new() { Text = "Cancel", AutoSize = true,
        DialogResult = DialogResult.Cancel };
    CancellationTokenSource? cancellation;
    bool allowClose;
    int liveProgressStart = -1;
    int liveProgressLength;

    internal CustomShowUrlDownload? Result { get; private set; }

    internal CustomShowUrlImportForm(CustomShowStore store)
    {
        this.store = store;
        Text = "Create Real-time Show from URL";
        ClientSize = new Size(780, 430);
        MinimumSize = new Size(650, 360);
        StartPosition = FormStartPosition.CenterParent;
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
            Padding = new Padding(14)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Video page URL", AutoSize = true,
            Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) }, 0, 0);
        layout.Controls.Add(url, 1, 0);
        cookies.Items.AddRange(["None (public URL)", "Chrome", "Edge",
            "Firefox", "Brave", CookieFileOption]);
        cookies.SelectedIndex = 0;
        layout.Controls.Add(new Label { Text = "Authentication", AutoSize = true,
            Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) }, 0, 1);
        layout.Controls.Add(cookies, 1, 1);
        TableLayoutPanel cookieFileRow = new()
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            Margin = Padding.Empty
        };
        cookieFileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cookieFileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cookieFileRow.Controls.Add(cookieFile, 0, 0);
        cookieFileRow.Controls.Add(browseCookieFile, 1, 0);
        layout.Controls.Add(cookieFileLabel, 0, 2);
        layout.Controls.Add(cookieFileRow, 1, 2);
        Label notice = new()
        {
            Text = "QuickPlayer downloads one video, opens the normal custom-show editor " +
                "with RVM ONNX selected, and retains the downloaded source inside the " +
                "published show. Close Chromium browsers before reading their cookies; " +
                "a cookies.txt file is a sensitive fallback and is never retained. Only " +
                "download videos you have permission to use.",
            AutoSize = true, MaximumSize = new Size(735, 0),
            Margin = new Padding(3, 10, 3, 10)
        };
        layout.Controls.Add(notice, 0, 3); layout.SetColumnSpan(notice, 2);
        layout.Controls.Add(output, 0, 4); layout.SetColumnSpan(output, 2);
        layout.Controls.Add(progress, 0, 5); layout.SetColumnSpan(progress, 2);
        FlowLayoutPanel buttons = new() { AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([download, setup, cancel]);
        layout.Controls.Add(buttons, 0, 6); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        download.Click += async (_, _) => await DownloadAsync();
        cookies.SelectedIndexChanged += (_, _) => UpdateCookieFileControls();
        browseCookieFile.Click += (_, _) => SelectCookieFile();
        setup.Click += (_, _) =>
        {
            using YtDlpSetupForm form = new();
            form.ShowDialog(this);
            UpdateSetupStatus();
        };
        FormClosing += (_, eventArgs) =>
        {
            if (cancellation == null || allowClose) return;
            eventArgs.Cancel = true;
            download.Text = "Cancelling...";
            cancellation.Cancel();
        };
        Shown += (_, _) => UpdateSetupStatus();
        AcceptButton = download;
        CancelButton = cancel;
        AppTheme.Apply(this);
        UpdateCookieFileControls();
    }

    void UpdateCookieFileControls()
    {
        bool enabled = string.Equals(cookies.SelectedItem?.ToString(),
            CookieFileOption, StringComparison.Ordinal);
        cookieFileLabel.Enabled = cookieFile.Enabled = browseCookieFile.Enabled = enabled;
    }

    void SelectCookieFile()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Select Netscape-format cookie file",
            Filter = "Cookie files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            cookieFile.Text = dialog.FileName;
    }

    void UpdateSetupStatus()
    {
        setup.Text = YtDlpSupport.IsInstalled
            ? "Update URL downloader..." : "Install URL downloader...";
        if (YtDlpSupport.IsInstalled && output.TextLength == 0)
        {
            string version = File.Exists(YtDlpSupport.VersionPath)
                ? File.ReadAllText(YtDlpSupport.VersionPath).Trim() : "installed";
            string denoVersion = File.Exists(YtDlpSupport.DenoVersionPath)
                ? File.ReadAllText(YtDlpSupport.DenoVersionPath).Trim() : "installed";
            Append($"yt-dlp {version} and Deno {denoVersion} are ready.");
        }
    }

    async Task DownloadAsync()
    {
        if (cancellation != null)
        {
            download.Text = "Cancelling...";
            cancellation.Cancel();
            return;
        }
        if (!YtDlpSupport.IsInstalled)
        {
            using YtDlpSetupForm setupForm = new();
            if (setupForm.ShowDialog(this) != DialogResult.OK) return;
        }
        bool usesCookieFile = string.Equals(cookies.SelectedItem?.ToString(),
            CookieFileOption, StringComparison.Ordinal);
        if (usesCookieFile && string.IsNullOrWhiteSpace(cookieFile.Text))
        {
            Append("Select a Netscape-format cookie file first.");
            return;
        }
        cancellation = new CancellationTokenSource();
        download.Text = "Cancel download";
        setup.Enabled = cancel.Enabled = url.Enabled = cookies.Enabled = false;
        browseCookieFile.Enabled = false;
        progress.Visible = true;
        liveProgressStart = -1;
        liveProgressLength = 0;
        output.Clear();
        try
        {
            string? browser = cookies.SelectedIndex == 0 || usesCookieFile ? null :
                cookies.SelectedItem?.ToString();
            Result = await YtDlpSupport.DownloadAsync(store, url.Text, browser,
                usesCookieFile ? cookieFile.Text : null,
                new Progress<string>(Append), cancellation.Token);
            allowClose = true;
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            Append("Download cancelled.");
        }
        catch (Exception error)
        {
            Append("Download failed: " + error.Message);
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            if (!IsDisposed)
            {
                download.Text = "Download and Create...";
                setup.Enabled = cancel.Enabled = url.Enabled = cookies.Enabled = true;
                UpdateCookieFileControls();
                progress.Visible = false;
            }
        }
    }

    void Append(string message)
    {
        if (IsDisposed) return;
        if (YtDlpSupport.IsLiveDownloadProgressLine(message))
        {
            if (liveProgressStart >= 0 &&
                liveProgressStart + liveProgressLength <= output.TextLength)
            {
                output.Select(liveProgressStart, liveProgressLength);
                output.SelectedText = message;
            }
            else
            {
                liveProgressStart = output.TextLength;
                output.AppendText(message);
            }
            liveProgressLength = message.Length;
        }
        else
        {
            if (liveProgressStart >= 0)
            {
                output.AppendText(Environment.NewLine);
                liveProgressStart = -1;
                liveProgressLength = 0;
            }
            output.AppendText(message + Environment.NewLine);
        }
        output.SelectionStart = output.TextLength;
        output.SelectionLength = 0;
        output.ScrollToCaret();
    }
}
