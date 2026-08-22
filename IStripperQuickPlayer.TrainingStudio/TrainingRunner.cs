using System.Diagnostics;
using System.Text.Json;

namespace IStripperQuickPlayer.TrainingStudio;

internal enum TrainingArchitecture
{
    ConvNextV2,
    Yolo26Semantic,
    Yolo26Instance
}

internal sealed class TrainingRunner
{
    const int TrainingRevision = 9;
    Process? process;
    internal event Action<string>? Message;
    internal event Action<string>? Progress;
    internal string? PackagePath { get; private set; }
    internal string? ReviewPath { get; private set; }
    internal string? BenchmarkPath { get; private set; }

    internal async Task<int> RunAsync(string datasetRoot, bool resume, int minimumResolution,
        TrainingArchitecture architecture, CancellationToken token)
    {
        bool yolo = architecture != TrainingArchitecture.ConvNextV2;
        int inputSize = yolo ? 1024 : 768;
        int revision = yolo ? 1 : TrainingRevision;
        string architectureId = architecture switch
        {
            TrainingArchitecture.Yolo26Semantic => "yolo26s-sem",
            TrainingArchitecture.Yolo26Instance => "yolo26s-seg",
            _ => "rvm-conditioned-convnext-fpn-v2"
        };
        string runs = Path.Combine(datasetRoot, "runs");
        string? resumable = resume && Directory.Exists(runs)
            ? Directory.EnumerateDirectories(runs)
                .Where(directory => (Directory.EnumerateFiles(directory, "last.pth",
                    SearchOption.AllDirectories).Any() || Directory.EnumerateFiles(directory, "last.pt",
                    SearchOption.AllDirectories).Any()) &&
                    ConfigurationMatches(directory, minimumResolution, architectureId, revision, inputSize))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        string output = resumable ?? Path.Combine(runs, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        if (resume && resumable == null) Message?.Invoke("No resumable checkpoint was found; starting a new run.");
        else if (resumable != null) Message?.Invoke($"Resuming {Path.GetFileName(resumable)} from its last checkpoint.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "training-config.json"), JsonSerializer.Serialize(new
        {
            trainingRevision = revision, architecture = architectureId,
            minimumResolution, inputSize, seed = 20260821,
            exportFormat = yolo ? 1 : (int?)null,
            ultralyticsVersion = yolo ? "8.4.126" : null
        }, new JsonSerializerOptions { WriteIndented = true }));
        string eventsPath = Path.Combine(output, "events.ndjson");
        string errorsPath = Path.Combine(output, "training.stderr.log");
        string consolePath = Path.Combine(output, "training.console.log");
        Message?.Invoke($"Writing training logs to {output}");
        string worker = Path.Combine(AppContext.BaseDirectory, "training",
            yolo ? "yolo26_benchmark.py" : "prop_segmenter_train_v2.py");
        ProcessStartInfo start = new(Sam2Client.RuntimePython())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (string argument in new[] { worker, "--dataset", datasetRoot, "--output", output })
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--minimum-resolution");
        start.ArgumentList.Add(minimumResolution.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--input-size");
        start.ArgumentList.Add(inputSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (yolo)
        {
            start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
            start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
            start.ArgumentList.Add("--variant");
            start.ArgumentList.Add(architectureId);
        }
        if (resumable != null) start.ArgumentList.Add("--resume");
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the training worker.");
        using CancellationTokenRegistration cancellationRegistration =
            token.Register(Cancel);
        await using StreamWriter events = new(eventsPath, append: resumable != null)
            { AutoFlush = true };
        await using StreamWriter errors = new(errorsPath, append: resumable != null)
            { AutoFlush = true };
        await using StreamWriter console = new(consolePath, append: resumable != null)
            { AutoFlush = true };
        Task errorPump = PumpAsync(process.StandardError, errors);
        int currentEpoch = 0, totalEpochs = 0;
        while (await process.StandardOutput.ReadLineAsync() is string line)
        {
            if (!line.TrimStart().StartsWith('{'))
            {
                string clean = StripTerminalFormatting(line);
                await console.WriteLineAsync(clean);
                if (TryFormatYoloProgress(clean, out string? progress,
                    out int parsedEpoch, out int parsedEpochs))
                {
                    currentEpoch = parsedEpoch; totalEpochs = parsedEpochs;
                    Progress?.Invoke(progress);
                }
                else if (TryFormatYoloValidationProgress(clean, currentEpoch, totalEpochs,
                    out progress)) Progress?.Invoke(progress);
                continue;
            }
            await events.WriteLineAsync(line);
            try
            {
                using JsonDocument json = JsonDocument.Parse(line);
                JsonElement root = json.RootElement;
                if (root.TryGetProperty("package", out JsonElement package)) PackagePath = package.GetString();
                if (root.TryGetProperty("review", out JsonElement review)) ReviewPath = review.GetString();
                if (root.TryGetProperty("benchmark", out JsonElement benchmark))
                    BenchmarkPath = benchmark.GetString();
                string stage = root.TryGetProperty("stage", out JsonElement stageValue) ? stageValue.GetString() ?? "" : "";
                string message = root.TryGetProperty("message", out JsonElement messageValue) ? messageValue.GetString() ?? "" : "";
                if (root.TryGetProperty("epoch", out JsonElement epoch))
                {
                    if (root.TryGetProperty("validationMeanIou", out JsonElement meanIou))
                    {
                        string pixelAccuracy = root.TryGetProperty("validationPixelAccuracy",
                            out JsonElement pixelAccuracyValue)
                            ? $" · pixel accuracy {pixelAccuracyValue.GetDouble():0.000}" : "";
                        Message?.Invoke($"epoch {epoch.GetInt32()}: mIoU {meanIou.GetDouble():0.000}" +
                            $"{pixelAccuracy} {message}".TrimEnd());
                        continue;
                    }
                    string dice = root.TryGetProperty("validationDice", out JsonElement value)
                        ? value.GetDouble().ToString("0.000") : "—";
                    string ratio = root.TryGetProperty("negativeRatio", out JsonElement ratioValue)
                        ? $"{ratioValue.GetDouble():P0} negatives · " : "";
                    string recall = root.TryGetProperty("validationRecall", out JsonElement recallValue)
                        ? $" · recall {recallValue.GetDouble():0.000}" : "";
                    string precision = root.TryGetProperty("validationPrecision", out JsonElement precisionValue)
                        ? $" · precision {precisionValue.GetDouble():0.000}" : "";
                    string exterior = root.TryGetProperty("validationExteriorPropDice",
                        out JsonElement exteriorValue) && exteriorValue.ValueKind == JsonValueKind.Number
                        ? $" · exterior {exteriorValue.GetDouble():0.000}" : "";
                    string retainedNegatives = root.TryGetProperty(
                        "validationRetainedNegativeFalsePositiveRate", out JsonElement retainedValue) &&
                        retainedValue.ValueKind == JsonValueKind.Number
                        ? $" · retained-neg FP {retainedValue.GetDouble():P0}" : "";
                    string threshold = root.TryGetProperty("validationThreshold", out JsonElement thresholdValue)
                        ? $" · threshold {thresholdValue.GetDouble():0.00}" : "";
                    Message?.Invoke($"{ratio}epoch {epoch.GetInt32()}: validation Dice {dice}" +
                        $"{precision}{recall}{exterior}{retainedNegatives}{threshold} {message}".TrimEnd());
                }
                else Message?.Invoke(($"{stage}: {message}").Trim(' ', ':'));
            }
            catch { Message?.Invoke(line); }
        }
        await process.WaitForExitAsync();
        await errorPump;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            Message?.Invoke($"Training failed; see {errorsPath}");
        return process.ExitCode;
    }

    static bool ConfigurationMatches(string run, int minimumResolution, string architecture,
        int revision, int inputSize)
    {
        string path = Path.Combine(run, "training-config.json");
        if (!File.Exists(path)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            int actualRevision = root.TryGetProperty("trainingRevision", out JsonElement revisionValue)
                ? revisionValue.GetInt32() : 1;
            return actualRevision == revision &&
                root.GetProperty("architecture").GetString() == architecture &&
                root.GetProperty("minimumResolution").GetInt32() == minimumResolution &&
                root.GetProperty("inputSize").GetInt32() == inputSize;
        }
        catch { return false; }
    }

    static async Task PumpAsync(StreamReader source, StreamWriter destination)
    {
        while (await source.ReadLineAsync() is string line)
            await destination.WriteLineAsync(line);
    }

    static string StripTerminalFormatting(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))", "");

    static bool TryFormatYoloProgress(string value, out string progress,
        out int parsedEpoch, out int parsedEpochs)
    {
        progress = ""; parsedEpoch = parsedEpochs = 0;
        System.Text.RegularExpressions.MatchCollection fractions =
            System.Text.RegularExpressions.Regex.Matches(value, @"(?<!\d)(\d+)/(\d+)(?!\d)");
        if (fractions.Count < 2 ||
            !int.TryParse(fractions[0].Groups[1].Value, out int epoch) ||
            !int.TryParse(fractions[0].Groups[2].Value, out int epochs) ||
            !int.TryParse(fractions[^1].Groups[1].Value, out int batch) ||
            !int.TryParse(fractions[^1].Groups[2].Value, out int batches) ||
            epochs < 2 || batches < 2 || batch < 0 || batch > batches) return false;
        int percent = (int)Math.Round(batch * 100d / batches);
        int filled = Math.Clamp((int)Math.Round(percent / 5d), 0, 20);
        string bar = new string('█', filled) + new string('░', 20 - filled);
        System.Text.RegularExpressions.Match speed =
            System.Text.RegularExpressions.Regex.Match(value, @"([0-9]+(?:\.[0-9]+)?)s/it");
        string remaining = "";
        if (speed.Success && double.TryParse(speed.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double secondsPerBatch))
        {
            TimeSpan eta = TimeSpan.FromSeconds(Math.Max(0, batches - batch) * secondsPerBatch);
            remaining = eta.TotalHours >= 1 ? $" · ~{eta:h\\:mm\\:ss} remaining" :
                $" · ~{eta:m\\:ss} remaining";
        }
        progress = $"Epoch {epoch}/{epochs}  [{bar}] {percent}%  · batch {batch}/{batches}{remaining}";
        parsedEpoch = epoch; parsedEpochs = epochs;
        return true;
    }

    static bool TryFormatYoloValidationProgress(string value, int epoch, int epochs,
        out string progress)
    {
        progress = "";
        if (epoch < 1 || epochs < 1 ||
            !(value.Contains("mIoU", StringComparison.OrdinalIgnoreCase) ||
              value.Contains("Mask(P", StringComparison.OrdinalIgnoreCase))) return false;
        System.Text.RegularExpressions.MatchCollection fractions =
            System.Text.RegularExpressions.Regex.Matches(value, @"(?<!\d)(\d+)/(\d+)(?!\d)");
        if (fractions.Count == 0 ||
            !int.TryParse(fractions[^1].Groups[1].Value, out int batch) ||
            !int.TryParse(fractions[^1].Groups[2].Value, out int batches) ||
            batches < 1 || batch < 0 || batch > batches) return false;
        int percent = (int)Math.Round(batch * 100d / batches);
        int filled = Math.Clamp((int)Math.Round(percent / 5d), 0, 20);
        string bar = new string('█', filled) + new string('░', 20 - filled);
        System.Text.RegularExpressions.Match speed =
            System.Text.RegularExpressions.Regex.Match(value, @"([0-9]+(?:\.[0-9]+)?)s/it");
        string remaining = "";
        if (speed.Success && double.TryParse(speed.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double secondsPerBatch))
        {
            TimeSpan eta = TimeSpan.FromSeconds(Math.Max(0, batches - batch) * secondsPerBatch);
            remaining = eta.TotalHours >= 1 ? $" · ~{eta:h\\:mm\\:ss} remaining" :
                $" · ~{eta:m\\:ss} remaining";
        }
        progress = $"Epoch {epoch}/{epochs} · validating  [{bar}] {percent}%" +
            $" · batch {batch}/{batches}{remaining}";
        return true;
    }

    internal void Cancel()
    {
        try { if (process is { HasExited: false }) process.Kill(true); } catch { }
    }

    internal static string? FindLatestPackage(string datasetRoot)
    {
        string runs = Path.Combine(datasetRoot, "runs");
        if (!Directory.Exists(runs)) return null;
        return Directory.EnumerateDirectories(runs)
            .Select(run => Path.Combine(run, "package"))
            .Where(package => File.Exists(Path.Combine(package, "manifest.json")) &&
                File.Exists(Path.Combine(package, "model.pth")))
            .OrderByDescending(package => File.GetLastWriteTimeUtc(Path.Combine(package, "manifest.json")))
            .FirstOrDefault();
    }

    internal static string InstallPackage(string packagePath)
    {
        string manifestPath = Path.Combine(packagePath, "manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        int schemaVersion = manifest.RootElement.GetProperty("schemaVersion").GetInt32();
        string? architecture = manifest.RootElement.GetProperty("architecture").GetString();
        if (!((schemaVersion == 1 && architecture == "deeplabv3-resnet50-binary-v1") ||
              (schemaVersion == 2 && architecture == "rvm-conditioned-convnext-fpn-v2")))
            throw new InvalidDataException("The package schema or architecture is unsupported.");
        if (schemaVersion == 2)
        {
            JsonElement input = manifest.RootElement.GetProperty("input");
            JsonElement runtime = manifest.RootElement.GetProperty("runtime");
            string[] channels = input.GetProperty("channels").EnumerateArray()
                .Select(value => value.GetString() ?? "").ToArray();
            if (input.GetProperty("cropSize").GetInt32() != 768 ||
                !channels.SequenceEqual(new[] { "red", "green", "blue", "rvmAlpha",
                    "rvmSignedDistance" }) ||
                runtime.GetProperty("contract").GetString() !=
                    "rvm-conditioned-temporal-union-v2")
                throw new InvalidDataException("The v2 input or postprocessing contract is invalid.");
        }
        string modelId = manifest.RootElement.GetProperty("modelId").GetString()
            ?? throw new InvalidDataException("The package model ID is missing.");
        if (modelId.Length is 0 or > 120 || modelId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidDataException("The package model ID is unsafe.");
        string expected = manifest.RootElement.GetProperty("checkpointSha256").GetString()
            ?? throw new InvalidDataException("The package checkpoint hash is missing.");
        string checkpoint = Path.Combine(packagePath, "model.pth");
        string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(checkpoint))).ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The package checkpoint hash is invalid.");
        JsonElement files = manifest.RootElement.GetProperty("files");
        string packageRoot = Path.GetFullPath(packagePath) + Path.DirectorySeparatorChar;
        foreach (JsonProperty item in files.EnumerateObject())
        {
            string file = Path.GetFullPath(Path.Combine(packagePath,
                item.Name.Replace('/', Path.DirectorySeparatorChar)));
            if (!file.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                throw new InvalidDataException($"The package file {item.Name} is missing or unsafe.");
            string fileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(file))).ToLowerInvariant();
            if (!fileHash.Equals(item.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The package file {item.Name} failed hash verification.");
        }
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer", "rvm-runtime", "prop-segmenter", "models");
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, modelId);
        if (Directory.Exists(destination)) throw new IOException($"Model {modelId} is already installed.");
        string temporary = destination + ".installing-" + Guid.NewGuid().ToString("N");
        try { CopyDirectory(packagePath, temporary); Directory.Move(temporary, destination); }
        catch
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
            throw;
        }
        return modelId;
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        foreach (string folder in Directory.EnumerateDirectories(source))
            CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }
}
