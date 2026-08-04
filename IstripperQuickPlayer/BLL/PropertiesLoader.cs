using IStripperQuickPlayer.DataModel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace IStripperQuickPlayer.BLL
{
    internal static class PropertiesLoader
    {
        private static readonly HttpClient client = new();
        internal static XmlDocument PropertiesXML = new XmlDocument();
        internal static XmlNodeList? cnode;

        internal static void loadXML()
        {
            FileInfo? path = null;
            try
            {
            path = findXMLFile();
            if (path == null || string.IsNullOrEmpty(path.FullName)) 
            {
                MetadataDiagnostics.RecordSourceFailure(
                    "release properties", null);
                MessageBox.Show("could not find Properties.xml file");
                return;
            }
            string fulltext = System.IO.File.ReadAllText(path.FullName);
            int start = fulltext.IndexOf('<');
            fulltext = fulltext.Substring(start);
            PropertiesXML = new XmlDocument();
            PropertiesXML.LoadXml(fulltext);
            if (PropertiesXML != null && PropertiesXML.ChildNodes != null)
            {
#pragma warning disable CS8601 // Possible null reference assignment.
                cnode = PropertiesXML.SelectNodes("/root/c");
#pragma warning restore CS8601 // Possible null reference assignment.
            }         
            }
            catch (Exception exception)
            {
                MetadataDiagnostics.RecordSourceFailure(
                    "release properties", path?.FullName, exception);
                throw;
            }
        }

        internal static CardProperties2? getCardByID(string ID)
        {
            XmlNode? node = getCardNodeByID(ID);
            return node == null ? null : new CardProperties2(node);
        }

        internal static string? getCardXmlByID(string ID) =>
            getCardNodeByID(ID)?.OuterXml;

        private static XmlNode? getCardNodeByID(string ID)
        {
            if (cnode == null)
                return null;
            ID = ID.Split('-')[0];
            foreach (XmlNode node in cnode)
                if (node.Attributes?["i"]?.Value == ID)
                    return node;
            return null;
        }

        private static FileInfo? findXMLFile()
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\System", false);
            string localapp = "";
            if (key != null)
            {
                var a = key.GetValue("DataPath", "");
                if (a != null)
                { 
                    localapp = a.ToString() ?? "";
                    key.Close();
                }
                else
                {
                    MessageBox.Show(@"Could not find registry key @CurrentUser\Software\Totem\vghd\System\DataPath ", "");
                }
                if (localapp == "")
                {
                    MessageBox.Show(@"Registry key @CurrentUser\Software\Totem\vghd\System\DataPath is empty?", "");
                }
            }
            else
            {
                MessageBox.Show(@"Could not find registry key @CurrentUser\Software\Totem\vghd\System", "");
            }
                       
            string fullpath = Path.Combine(localapp, "Properties loaded from server.xml");
            if (File.Exists(fullpath))
            {
                return new FileInfo(fullpath);
            }

                //we need to get it from the server
                string url = @"http://www.istripper.com/bof/properties/properties_iStripper.xml.gz";
                DownloadGZFile(url, fullpath);
                return new FileInfo(fullpath);
        }

        private static void DownloadGZFile(string url, string DecompressedFileName)
        {
            string path = Path.Join(Path.GetTempPath(), "Properties_iStripper.xml.gz");
            File.WriteAllBytes(path,
                client.GetByteArrayAsync(url).GetAwaiter().GetResult());
            using Stream inStream = File.OpenRead(path);
            using FileStream outputFileStream = File.Create(DecompressedFileName);
            using var decompressor = new GZipStream(inStream, CompressionMode.Decompress);
            decompressor.CopyTo(outputFileStream);
        }
    }

    internal static class MetadataRefresh
    {
        private sealed record Feed(
            string Url, string FileName, string[] RequiredPaths);

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        private static readonly Feed[] Feeds =
        [
            new(
                "https://www.istripper.com/bof/properties/properties_iStripper.xml.gz",
                "Properties loaded from server.xml", ["/root/c"]),
            new(
                "https://www.istripper.com/bof/mselistGenerator/staticProperties_iStripper.xml.gz",
                "staticProperties loaded from server.xml",
                ["/root/c", "/root/m"])
        ];

        internal static async Task RefreshAsync(
            CancellationToken cancellationToken = default)
        {
            string dataPath = GetDataPath();
            Directory.CreateDirectory(dataPath);
            string operation = Guid.NewGuid().ToString("N");
            List<(Feed Feed, string Temporary)> downloads = [];
            List<(string Target, string Backup, bool HadOriginal)> replaced = [];
            bool completed = false;

            try
            {
                foreach (Feed feed in Feeds)
                {
                    string temporary = Path.Combine(
                        dataPath, $".{feed.FileName}.{operation}.tmp");
                    await DownloadAsync(
                        feed.Url, temporary, cancellationToken);
                    ValidateXml(
                        File.ReadAllText(temporary), feed.RequiredPaths);
                    downloads.Add((feed, temporary));
                }

                foreach ((Feed feed, string temporary) in downloads)
                {
                    string target = Path.Combine(dataPath, feed.FileName);
                    string backup = target + $".{operation}.bak";
                    bool hadOriginal = File.Exists(target);
                    if (hadOriginal)
                        File.Replace(temporary, target, backup, true);
                    else
                        File.Move(temporary, target);
                    replaced.Add((target, backup, hadOriginal));
                }
                completed = true;
            }
            catch
            {
                foreach ((string target, string backup, bool hadOriginal) in
                         replaced.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (hadOriginal && File.Exists(backup))
                        {
                            File.Delete(target);
                            File.Move(backup, target);
                        }
                        else if (!hadOriginal)
                        {
                            File.Delete(target);
                        }
                    }
                    catch
                    {
                        // Keep the backup if Windows prevents rollback.
                    }
                }
                throw;
            }
            finally
            {
                foreach ((_, string temporary) in downloads)
                    File.Delete(temporary);
                if (completed)
                    foreach ((_, string backup, _) in replaced)
                        File.Delete(backup);
            }
        }

        private static async Task DownloadAsync(
            string url, string destination,
            CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true
            };
            using HttpResponseMessage response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using Stream source =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            await using GZipStream decompressor = new(
                source, CompressionMode.Decompress);
            await using FileStream output = new(
                destination, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            await decompressor.CopyToAsync(output, cancellationToken);
        }

        private static string GetDataPath()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Totem\vghd\System", false);
            string path = key?.GetValue(
                "DataPath", "")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    "iStripper's data folder is not configured.");
            return Path.GetFullPath(path);
        }

        private static void ValidateXml(
            string text, IEnumerable<string> requiredPaths)
        {
            int start = text.IndexOf('<');
            if (start < 0)
                throw new InvalidDataException(
                    "The downloaded metadata does not contain XML.");
            XmlDocument document = new() { XmlResolver = null };
            document.LoadXml(text[start..]);
            foreach (string path in requiredPaths)
                if (document.SelectNodes(path)?.Count is not > 0)
                    throw new InvalidDataException(
                        $"The downloaded metadata is missing {path}.");
        }

        internal static bool VerifyValidation()
        {
            try
            {
                ValidateXml("header<root><c i='x'/><m id='1'/></root>",
                    ["/root/c", "/root/m"]);
                try
                {
                    ValidateXml(
                        "<root><c i='x'/></root>", ["/root/m"]);
                    return false;
                }
                catch (InvalidDataException)
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    internal static class MetadataDiagnostics
    {
        private const int MaxDetailedIssues = 500;
        private static readonly object Sync = new();
        private static StringBuilder? report;
        private static Dictionary<string, int>? issueCounts;
        private static int totalIssues;

        internal static string FilePath => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer", "metadata-diagnostic.txt");

        internal static void Begin()
        {
            lock (Sync)
            {
                report = new StringBuilder();
                issueCounts = new(StringComparer.Ordinal);
                totalIssues = 0;
                report.AppendLine("iStripper QuickPlayer metadata diagnostic");
                report.AppendLine($"Created UTC: {DateTime.UtcNow:O}");
                report.AppendLine($"QuickPlayer version: {Application.ProductVersion}");
                report.AppendLine($"OS: {Environment.OSVersion.VersionString}");
                report.AppendLine($".NET: {Environment.Version}; process: " +
                    (Environment.Is64BitProcess ? "64-bit" : "32-bit"));
                report.AppendLine($"Culture: {CultureInfo.CurrentCulture.Name}; " +
                    $"region uses metric: {RegionInfo.CurrentRegion.IsMetric}");
                report.AppendLine(
                    "Privacy: no usernames, full paths, account state, " +
                    "credentials, keys or tokens are included.");
                report.AppendLine();

            }
        }

        internal static void Record(
            string? cardTag, string stage, string reason,
            string? filePath = null, string? modelId = null,
            string? modelName = null, Exception? exception = null,
            string? xmlContext = null)
        {
            lock (Sync)
            {
                if (report == null)
                    return;
                AddIssue(cardTag, stage, reason, filePath,
                    modelId, modelName, exception, xmlContext);
            }
        }

        internal static void Complete(int catalogueVersion)
        {
            lock (Sync)
            {
                if (report == null)
                    return;
                if (totalIssues > 0)
                {
                    string? dataPath = TryGetDataPath();
                    if (dataPath == null)
                        AddIssue(null, "metadata sources",
                            "iStripper data folder is unavailable");
                    else
                    {
                        AppendSource(dataPath,
                            "Properties loaded from server.xml");
                        AppendSource(dataPath,
                            "staticProperties loaded from server.xml");
                    }
                }
                report.AppendLine();
                report.AppendLine($"Catalogue format version: {catalogueVersion}");
                report.AppendLine($"Total issues: {totalIssues}");
                if (issueCounts != null)
                    foreach ((string issue, int count) in issueCounts)
                        report.AppendLine($"  {issue}: {count}");
                if (totalIssues > MaxDetailedIssues)
                    report.AppendLine(
                        $"Only the first {MaxDetailedIssues} issues are detailed.");

                if (totalIssues > 0)
                {
                    try
                    {
                        Directory.CreateDirectory(
                            Path.GetDirectoryName(FilePath)!);
                        File.WriteAllText(FilePath, report.ToString(),
                            new UTF8Encoding(false));
                    }
                    catch
                    {
                        // Diagnostics must never stop the catalogue loading.
                    }
                }
                report = null;
                issueCounts = null;
            }
        }

        internal static void RecordRefreshFailure(Exception exception)
        {
            Begin();
            Record(null, "metadata refresh", "refresh failed",
                exception: exception);
            Complete(Datastore.versionnumber);
        }

        internal static void RecordSourceFailure(
            string stage, string? filePath, Exception? exception = null)
        {
            Begin();
            Record(null, stage, "metadata file could not be read",
                filePath: filePath, exception: exception);
            Complete(Datastore.versionnumber);
        }

        private static void AddIssue(
            string? cardTag, string stage, string reason,
            string? filePath = null, string? modelId = null,
            string? modelName = null, Exception? exception = null,
            string? xmlContext = null)
        {
            if (report == null || issueCounts == null)
                return;
            totalIssues++;
            string key = Safe(stage) + " / " + Safe(reason);
            issueCounts[key] = issueCounts.GetValueOrDefault(key) + 1;
            if (totalIssues > MaxDetailedIssues)
                return;

            StringBuilder line = new($"Issue {totalIssues}: " +
                $"stage={Safe(stage)}; reason={Safe(reason)}");
            if (!string.IsNullOrWhiteSpace(cardTag))
                line.Append($"; card={Safe(cardTag)}");
            if (!string.IsNullOrWhiteSpace(modelId))
                line.Append($"; model-id={Safe(modelId)}");
            if (!string.IsNullOrWhiteSpace(modelName))
                line.Append($"; model-name={Safe(modelName)}");
            if (!string.IsNullOrWhiteSpace(filePath))
                AppendFileDetails(line, filePath);
            if (exception != null)
                line.Append($"; exception={ExceptionSummary(exception)}");
            if (!string.IsNullOrWhiteSpace(filePath) &&
                FindXmlException(exception) is XmlException xml)
                AppendXmlFragment(line, filePath, xml);
            if (!string.IsNullOrWhiteSpace(xmlContext))
            {
                string context = SanitizeXmlFragment(xmlContext, 2_000);
                if (context.Length > 0)
                    line.Append($"; xml-context={context}");
            }
            report.AppendLine(line.ToString());
        }

        private static void AppendSource(string dataPath, string fileName)
        {
            string path = Path.Combine(dataPath, fileName);
            if (!File.Exists(path))
            {
                AddIssue(null, "metadata source", "file is missing", path);
                return;
            }

            try
            {
                FileInfo file = new(path);
                using FileStream stream = File.OpenRead(path);
                string hash = Convert.ToHexString(SHA256.HashData(stream));
                string text = File.ReadAllText(path);
                int start = text.IndexOf('<');
                if (start < 0)
                    throw new InvalidDataException("XML start not found.");
                XmlDocument document = new() { XmlResolver = null };
                document.LoadXml(text[start..]);
                report!.AppendLine(
                    $"Source: file={fileName}; bytes={file.Length}; " +
                    $"modified-utc={file.LastWriteTimeUtc:O}; sha256={hash}; " +
                    $"prefix-bytes={start}; cards=" +
                    $"{document.SelectNodes("/root/c")?.Count ?? 0}; models=" +
                    $"{document.SelectNodes("/root/m")?.Count ?? 0}; bios=" +
                    $"{document.SelectNodes("/root/d")?.Count ?? 0}");
            }
            catch (Exception exception)
            {
                AddIssue(null, "metadata source", "file could not be read",
                    path, exception: exception);
            }
        }

        private static void AppendFileDetails(
            StringBuilder line, string filePath)
        {
            line.Append($"; file={SafeFileName(filePath)}");
            try
            {
                FileInfo file = new(filePath);
                line.Append($"; exists={file.Exists}");
                if (!file.Exists)
                    return;
                line.Append($"; bytes={file.Length}; " +
                    $"modified-utc={file.LastWriteTimeUtc:O}");
                using FileStream stream = File.OpenRead(filePath);
                line.Append($"; sha256=" +
                    Convert.ToHexString(SHA256.HashData(stream)));
            }
            catch (Exception exception)
            {
                line.Append($"; inspection={ExceptionSummary(exception)}");
            }
        }

        private static string? TryGetDataPath()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Totem\vghd\System", false);
                string path = key?.GetValue(
                    "DataPath", "")?.ToString() ?? "";
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }

        private static string ExceptionSummary(Exception exception)
        {
            StringBuilder value = new();
            for (Exception? current = exception;
                 current != null && value.Length < 400;
                 current = current.InnerException)
            {
                if (value.Length > 0)
                    value.Append(" -> ");
                value.Append(current.GetType().FullName)
                    .Append(" (HRESULT 0x")
                    .Append(current.HResult.ToString("X8"))
                    .Append(')');
                if (current is XmlException xml)
                    value.Append($" line={xml.LineNumber},position={xml.LinePosition}");
            }
            return value.ToString();
        }

        private static XmlException? FindXmlException(Exception? exception)
        {
            while (exception != null)
            {
                if (exception is XmlException xml)
                    return xml;
                exception = exception.InnerException;
            }
            return null;
        }

        private static void AppendXmlFragment(
            StringBuilder line, string filePath, XmlException exception)
        {
            if (exception.LineNumber < 1 || exception.LinePosition < 1)
                return;
            try
            {
                using StreamReader reader = File.OpenText(filePath);
                string? xmlLine = null;
                for (int number = 1; number <= exception.LineNumber; number++)
                    xmlLine = reader.ReadLine();
                if (xmlLine == null)
                    return;
                const int radius = 240;
                int position = Math.Clamp(
                    exception.LinePosition - 1, 0, xmlLine.Length);
                int start = Math.Max(0, position - radius);
                int length = Math.Min(
                    xmlLine.Length - start, radius * 2);
                string fragment = SanitizeXmlFragment(
                    xmlLine.Substring(start, length));
                if (fragment.Length > 0)
                    line.Append($"; xml-fragment={fragment}");
            }
            catch (Exception fragmentException)
            {
                line.Append($"; fragment-inspection=" +
                    ExceptionSummary(fragmentException));
            }
        }

        private static string SanitizeXmlFragment(
            string fragment, int maximumLength = 200)
        {
            string safe = Regex.Replace(fragment,
                @"(?<name>\b(?:access[_-]?token|api[_-]?key|token|secret|pass(?:word|wd)?|authorization|auth|account(?:id)?|user(?:name|id)?|email|session(?:id)?|cookie|license(?:key)?|path))(?<equals>\s*=\s*)(?<quote>[""'])(?<value>.*?)(\k<quote>)",
                match => match.Groups["name"].Value +
                    match.Groups["equals"].Value +
                    match.Groups["quote"].Value + "[redacted]" +
                    match.Groups["quote"].Value,
                RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            safe = Regex.Replace(safe,
                @"<(?<name>access[_-]?token|api[_-]?key|token|secret|pass(?:word|wd)?|authorization|auth|account(?:id)?|user(?:name|id)?|email|session(?:id)?|cookie|license(?:key)?|path)\b[^>]*>.*?</\k<name>\s*>",
                match => $"<{match.Groups["name"].Value}>[redacted]</" +
                    match.Groups["name"].Value + ">",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(100));
            safe = Regex.Replace(safe,
                @"(?i)\bBearer\s+[^\s<>'"";]+", "Bearer [redacted]",
                RegexOptions.None, TimeSpan.FromMilliseconds(100));
            safe = Regex.Replace(safe,
                @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
                "[redacted-email]", RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100));
            safe = Regex.Replace(safe,
                @"(?i)(?:[a-z]:\\|\\\\)[^\s<>'""]+",
                "[redacted-path]", RegexOptions.None,
                TimeSpan.FromMilliseconds(100));
            return Safe(safe, maximumLength);
        }

        private static string Safe(string value, int maximumLength = 200)
        {
            string safe = value.Replace('\r', ' ').Replace('\n', ' ')
                .Replace('\t', ' ').Trim();
            return safe.Length <= maximumLength
                ? safe : safe[..maximumLength];
        }

        private static string SafeFileName(string path)
        {
            try
            {
                return Safe(Path.GetFileName(path));
            }
            catch
            {
                return "unavailable";
            }
        }

        internal static bool VerifyPrivacy()
        {
            const string privatePath = @"C:\Users\Example Person\secret.xml";
            string exception = ExceptionSummary(
                new IOException("token=secret at " + privatePath));
            string xml = SanitizeXmlFragment(
                "<c id=\"f1234\" token=\"secret\" " +
                "email='person@example.com' path=\"C:\\Users\\Person\\file\" />");
            return SafeFileName(privatePath) == "secret.xml" &&
                !exception.Contains("Example Person", StringComparison.Ordinal) &&
                !exception.Contains("token", StringComparison.OrdinalIgnoreCase) &&
                Safe("one\r\ntwo") == "one  two" &&
                xml.Contains("id=\"f1234\"", StringComparison.Ordinal) &&
                !xml.Contains("secret", StringComparison.Ordinal) &&
                !xml.Contains("person@example.com", StringComparison.Ordinal) &&
                !xml.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase);
        }
    }
}
