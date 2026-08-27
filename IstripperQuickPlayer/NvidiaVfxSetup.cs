using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IStripperQuickPlayer;

internal static class NvidiaVfxSetup
{
    const string ResourceEndpoint =
        "https://api.ngc.nvidia.com/v2/org/nvidia/team/maxine/resources/vfx_sdk_core";
    internal const string CatalogAccessUrl =
        "https://catalog.ngc.nvidia.com/orgs/nvidia/maxine/resources/vfx_sdk_core/-";
    internal static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "nvidia-vfx-sdk");

    internal static async Task<string> InstallAsync(string apiKey,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException(
                "NVIDIA Video Effects SDK requires 64-bit Windows.");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidDataException("Enter an NGC personal API key.");
        ValidateGpuAndDriver();
        Directory.CreateDirectory(InstallRoot);
        string staging = Path.Combine(InstallRoot,
            ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using HttpClient client = new();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            progress?.Report("Resolving the latest Windows VFX SDK Core...");
            using JsonDocument versions = await GetJson(client,
                ResourceEndpoint + "/versions", "list VFX SDK versions", token);
            string version = SelectLatestWindowsVersion(versions.RootElement) ??
                throw new InvalidDataException(
                    "NGC did not return a Windows VFX SDK Core version.");
            string destination = Path.Combine(InstallRoot, version);
            string candidate = Path.Combine(InstallRoot, ".candidate-" + version);
            if (Directory.Exists(destination))
            {
                ValidateInstallation(destination);
                return destination;
            }
            if (Directory.Exists(candidate))
            {
                progress?.Report("Validating the previously downloaded NVIDIA SDK...");
                try
                {
                    ValidateInstallation(candidate);
                    Directory.Move(candidate, destination);
                    progress?.Report($"NVIDIA AI Green Screen {version} is ready.");
                    return destination;
                }
                catch { }
            }
            string zip = Path.Combine(staging, "vfx-sdk-core.zip");
            string versionEndpoint = ResourceEndpoint + "/versions/" +
                Uri.EscapeDataString(version);
            string download = versionEndpoint + "/zip";
            try
            {
                using JsonDocument files = await GetJson(client,
                    versionEndpoint + "/files", "list VFX SDK package files", token);
                string? package = SelectWindowsPackage(files.RootElement);
                if (package != null)
                    download = versionEndpoint + "/files/" +
                        EscapeNgcPath(package) + "?download=true";
            }
            catch (NgcFileListingUnavailableException)
            {
                // Older NGC registries do not expose a file listing. Their
                // whole-resource ZIP endpoint remains the supported fallback.
            }
            progress?.Report($"Downloading NVIDIA VFX SDK Core {version}...");
            using HttpResponseMessage response = await client.GetAsync(download,
                HttpCompletionOption.ResponseHeadersRead, token);
            await EnsureNgcSuccess(response, "download the VFX SDK Core", token);
            await using (Stream input = await response.Content.ReadAsStreamAsync(token))
            await using (FileStream output = File.Create(zip))
                await input.CopyToAsync(output, token);
            string extracted = Path.Combine(staging, "extracted");
            progress?.Report("Extracting the SDK Core...");
            ZipFile.ExtractToDirectory(zip, extracted);
            string featureScript = Directory.EnumerateFiles(extracted,
                "install_feature.ps1", SearchOption.AllDirectories)
                .FirstOrDefault() ?? throw new FileNotFoundException(
                    "The SDK Core feature installer is missing.");
            string sdkRoot = Directory.GetParent(
                Path.GetDirectoryName(featureScript)!)!.FullName;
            progress?.Report("Installing NVIDIA AI Green Screen libraries and models...");
            await RunFeatureInstaller(featureScript, apiKey, progress, token);
            if (Directory.Exists(candidate)) Directory.Delete(candidate, true);
            Directory.Move(sdkRoot, candidate);
            ValidateInstallation(candidate);
            Directory.Move(candidate, destination);
            progress?.Report($"NVIDIA AI Green Screen {version} is ready.");
            return destination;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch { }
        }
    }

    internal static string? SelectLatestWindowsVersion(JsonElement root)
    {
        List<string> values = [];
        CollectStrings(root, values);
        return values.Where(value => Regex.IsMatch(value,
                @"^\d+(?:\.\d+){1,3}_windows$", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => ParseVersion(value))
            .FirstOrDefault();
    }

    internal static string? SelectWindowsPackage(JsonElement root)
    {
        List<string> values = [];
        CollectStrings(root, values);
        return values.Select(NormalizeNgcPath)
            .Where(value => value != null &&
                value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Contains("windows",
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(value => value.Contains("vfx",
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(value => value.Length)
            .FirstOrDefault();
    }

    static string? NormalizeNgcPath(string value)
    {
        value = value.Trim().Replace('\\', '/').TrimStart('/');
        if (value.Length == 0 || Uri.IsWellFormedUriString(value,
                UriKind.Absolute) || value.Split('/').Any(part =>
                    part is "" or "." or ".."))
            return null;
        return value;
    }

    internal static string EscapeNgcPath(string value) => string.Join('/',
        value.Split('/').Select(Uri.EscapeDataString));

    static async Task<JsonDocument> GetJson(HttpClient client, string url,
        string operation, CancellationToken token)
    {
        using HttpResponseMessage response = await client.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound &&
            operation.Contains("package files", StringComparison.Ordinal))
            throw new NgcFileListingUnavailableException();
        await EnsureNgcSuccess(response, operation, token);
        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    static async Task EnsureNgcSuccess(HttpResponseMessage response,
        string operation, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        await response.Content.LoadIntoBufferAsync(token);
        string guidance = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                "The NGC personal API key was rejected. Create an active personal " +
                "key with the NGC Catalog service enabled and try again.",
            System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound =>
                "The NGC account or personal key does not have access to this protected " +
                "SDK package. Sign in to the NVIDIA VFX SDK Core catalog page, choose " +
                "Get Access, complete the NVIDIA Developer Program subscription, then " +
                "create a new personal key with the NGC Catalog service enabled.",
            _ => $"NVIDIA NGC returned HTTP {(int)response.StatusCode} " +
                $"({response.ReasonPhrase})."
        };
        throw new HttpRequestException($"Could not {operation}. {guidance} " +
            $"Access: {CatalogAccessUrl}", null, response.StatusCode);
    }

    sealed class NgcFileListingUnavailableException : Exception { }

    static void CollectStrings(JsonElement element, List<string> values)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            if (value != null) values.Add(value);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray())
                CollectStrings(child, values);
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty child in element.EnumerateObject())
                CollectStrings(child.Value, values);
    }

    static Version ParseVersion(string value) =>
        Version.TryParse(value[..value.IndexOf('_')], out Version? version)
            ? version : new Version();

    static async Task RunFeatureInstaller(string script, string apiKey,
        IProgress<string>? progress, CancellationToken token)
    {
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy",
            "Bypass", "-File", script, "-features", "nvvfxgreenscreen" })
            start.ArgumentList.Add(argument);
        start.Environment["NGC_CLI_API_KEY"] = apiKey;
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException(
                "The NVIDIA feature installer could not start.");
        start.Environment["NGC_CLI_API_KEY"] = "";
        using CancellationTokenRegistration cancellation = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        Task output = Pump(process.StandardOutput, apiKey, progress);
        Task errors = Pump(process.StandardError, apiKey, progress);
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(output, errors);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"The NVIDIA AI Green Screen feature installer failed " +
                $"(exit code {process.ExitCode}).");
    }

    static async Task Pump(StreamReader reader, string secret,
        IProgress<string>? progress)
    {
        while (await reader.ReadLineAsync() is string line)
            progress?.Report(Redact(line, secret));
    }

    internal static string Redact(string value, string? secret) =>
        string.IsNullOrEmpty(secret) ? value : value.Replace(secret,
            "[redacted]", StringComparison.Ordinal);

    internal static void ValidateInstallation(string root)
    {
        ValidateGpuAndDriver();
        using NvidiaAiGreenScreenSession test = new(root, 512, 288,
            new CustomNvidiaSettings { Temporal = false });
        byte[] bgr = new byte[512 * 288 * 3];
        byte[] alpha = new byte[512 * 288];
        test.Run(bgr, alpha);
    }

    static void ValidateGpuAndDriver()
    {
        ProcessStartInfo start = new("nvidia-smi")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--query-gpu=compute_cap,driver_version");
        start.ArgumentList.Add("--format=csv,noheader");
        using Process process = Process.Start(start) ??
            throw new PlatformNotSupportedException("NVIDIA GPU detection failed.");
        string? line = process.StandardOutput.ReadLine();
        if (!process.WaitForExit(5000) || process.ExitCode != 0 || line == null)
            throw new PlatformNotSupportedException(
                "A supported NVIDIA Tensor Core GPU was not detected.");
        string[] values = line.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length < 2 || !Version.TryParse(values[0], out Version? compute) ||
            compute < new Version(7, 5))
            throw new PlatformNotSupportedException(
                "NVIDIA AI Green Screen requires a Turing-or-newer Tensor Core GPU.");
        if (!Version.TryParse(values[1], out Version? driver) ||
            driver < new Version(570, 65))
            throw new PlatformNotSupportedException(
                "NVIDIA driver 570.65 or newer is required.");
    }

    internal static bool VerifyContracts()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"versions\":[{\"versionId\":\"1.1.0.0_windows\"}," +
            "{\"versionId\":\"1.2.0.0_linux\"},{\"versionId\":\"1.2.0.0_windows\"}]}");
        using JsonDocument files = JsonDocument.Parse(
            "{\"files\":[{\"path\":\"docs/readme.zip\"}," +
            "{\"path\":\"packages/NVIDIA VFX SDK Core Windows.zip\"}]}");
        return SelectLatestWindowsVersion(document.RootElement) ==
                "1.2.0.0_windows" &&
            SelectWindowsPackage(files.RootElement) ==
                "packages/NVIDIA VFX SDK Core Windows.zip" &&
            EscapeNgcPath("packages/NVIDIA VFX SDK Core Windows.zip") ==
                "packages/NVIDIA%20VFX%20SDK%20Core%20Windows.zip" &&
            Redact("key=secret-value", "secret-value") == "key=[redacted]";
    }
}

internal sealed class NvidiaVfxSetupForm : Form
{
    readonly CustomShowConfiguration configuration;
    readonly TextBox apiKey = new() { UseSystemPasswordChar = true, Width = 430 };
    readonly CheckBox terms = new() { AutoSize = true, Text =
        "I acknowledge the NVIDIA SDK, AI product, and model terms." };
    readonly TextBox status = new() { Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly Button install = new() { Text = "Install", AutoSize = true };
    readonly CancellationTokenSource cancellation = new();

    internal NvidiaVfxSetupForm(CustomShowConfiguration configuration)
    {
        this.configuration = configuration;
        Text = "Install NVIDIA AI Green Screen";
        ClientSize = new Size(720, 430);
        StartPosition = FormStartPosition.CenterParent;
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding =
            new Padding(14), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(680, 0),
            Text = "Requires 64-bit Windows, a Turing-or-newer NVIDIA Tensor Core GPU, " +
                "driver 570.65+, and an NGC account subscribed to the VFX SDK. " +
                "The personal key must include the NGC Catalog service." });
        layout.Controls.Add(new Label { Text = "NGC personal API key", AutoSize = true });
        layout.Controls.Add(apiKey);
        layout.Controls.Add(terms);
        FlowLayoutPanel links = new() { AutoSize = true };
        links.Controls.Add(Link("Open NGC VFX SDK access",
            NvidiaVfxSetup.CatalogAccessUrl));
        links.Controls.Add(Link("Create a personal API key",
            "https://org.ngc.nvidia.com/setup/personal-keys"));
        links.Controls.Add(Link("Read NVIDIA VFX SDK terms",
            "https://docs.nvidia.com/maxine/vfx/latest/WindowsVFXSDK/InstalltheVFXSDK.html"));
        layout.Controls.Add(links);
        layout.Controls.Add(status);
        FlowLayoutPanel buttons = new() { AutoSize = true };
        buttons.Controls.Add(install);
        buttons.Controls.Add(new Button { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        install.Click += async (_, _) => await Install();
        FormClosed += (_, _) => { cancellation.Cancel(); apiKey.Clear(); };
        AppTheme.Apply(this);
    }

    static LinkLabel Link(string text, string url)
    {
        LinkLabel link = new() { Text = text, AutoSize = true,
            Margin = new Padding(0, 3, 14, 3) };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(url)
            { UseShellExecute = true });
        return link;
    }

    async Task Install()
    {
        if (!terms.Checked)
        {
            MessageBox.Show(this, "Acknowledge NVIDIA's SDK and model terms first.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string secret = apiKey.Text;
        apiKey.Clear();
        install.Enabled = false;
        try
        {
            Progress<string> progress = new(line => status.AppendText(
                NvidiaVfxSetup.Redact(line, secret) + Environment.NewLine));
            string root = await NvidiaVfxSetup.InstallAsync(secret, progress,
                cancellation.Token);
            configuration.NvidiaVfxSdkRoot = root;
            configuration.Save();
            secret = "";
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            status.AppendText(NvidiaVfxSetup.Redact(error.Message, secret) +
                Environment.NewLine);
            install.Enabled = true;
        }
        finally { secret = ""; apiKey.Clear(); }
    }
}
