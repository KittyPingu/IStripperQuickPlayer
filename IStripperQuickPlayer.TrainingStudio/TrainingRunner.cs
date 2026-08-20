using System.Diagnostics;
using System.Text.Json;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class TrainingRunner
{
    const int TrainingRevision = 3;
    Process? process;
    internal event Action<string>? Message;
    internal string? PackagePath { get; private set; }
    internal string? ReviewPath { get; private set; }

    internal async Task<int> RunAsync(string datasetRoot, bool resume, int minimumResolution,
        int inputSize, string negativeSelection, CancellationToken token)
    {
        string runs = Path.Combine(datasetRoot, "runs");
        string? resumable = resume && Directory.Exists(runs)
            ? Directory.EnumerateDirectories(runs)
                .Where(directory => Directory.EnumerateFiles(directory, "last.pth",
                    SearchOption.AllDirectories).Any() &&
                    ConfigurationMatches(directory, minimumResolution, inputSize, negativeSelection))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        string output = resumable ?? Path.Combine(runs, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        if (resume && resumable == null) Message?.Invoke("No resumable checkpoint was found; starting a new run.");
        else if (resumable != null) Message?.Invoke($"Resuming {Path.GetFileName(resumable)} from its last checkpoint.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "training-config.json"), JsonSerializer.Serialize(new
        {
            trainingRevision = TrainingRevision, minimumResolution, inputSize, negativeSelection
        }, new JsonSerializerOptions { WriteIndented = true }));
        string eventsPath = Path.Combine(output, "events.ndjson");
        string errorsPath = Path.Combine(output, "training.stderr.log");
        Message?.Invoke($"Writing training logs to {output}");
        string worker = Path.Combine(AppContext.BaseDirectory, "training", "prop_segmenter_train.py");
        ProcessStartInfo start = new(Sam2Client.RuntimePython())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { worker, "--dataset", datasetRoot, "--output", output })
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--minimum-resolution");
        start.ArgumentList.Add(minimumResolution.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--input-size");
        start.ArgumentList.Add(inputSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--negative-selection");
        start.ArgumentList.Add(negativeSelection);
        if (resumable != null) start.ArgumentList.Add("--resume");
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the training worker.");
        using CancellationTokenRegistration cancellationRegistration =
            token.Register(Cancel);
        await using StreamWriter events = new(eventsPath, append: resumable != null)
            { AutoFlush = true };
        await using StreamWriter errors = new(errorsPath, append: resumable != null)
            { AutoFlush = true };
        Task errorPump = PumpAsync(process.StandardError, errors);
        while (await process.StandardOutput.ReadLineAsync() is string line)
        {
            await events.WriteLineAsync(line);
            try
            {
                using JsonDocument json = JsonDocument.Parse(line);
                JsonElement root = json.RootElement;
                if (root.TryGetProperty("package", out JsonElement package)) PackagePath = package.GetString();
                if (root.TryGetProperty("review", out JsonElement review)) ReviewPath = review.GetString();
                string stage = root.TryGetProperty("stage", out JsonElement stageValue) ? stageValue.GetString() ?? "" : "";
                string message = root.TryGetProperty("message", out JsonElement messageValue) ? messageValue.GetString() ?? "" : "";
                if (root.TryGetProperty("epoch", out JsonElement epoch))
                {
                    string dice = root.TryGetProperty("validationDice", out JsonElement value)
                        ? value.GetDouble().ToString("0.000") : "—";
                    string ratio = root.TryGetProperty("negativeRatio", out JsonElement ratioValue)
                        ? $"{ratioValue.GetDouble():P0} negatives · " : "";
                    string recall = root.TryGetProperty("validationRecall", out JsonElement recallValue)
                        ? $" · recall {recallValue.GetDouble():0.000}" : "";
                    string precision = root.TryGetProperty("validationPrecision", out JsonElement precisionValue)
                        ? $" · precision {precisionValue.GetDouble():0.000}" : "";
                    string union = root.TryGetProperty("validationRvmUnionDice", out JsonElement unionValue) &&
                        unionValue.ValueKind == JsonValueKind.Number
                        ? $" · RVM union {unionValue.GetDouble():0.000}" : "";
                    string threshold = root.TryGetProperty("validationThreshold", out JsonElement thresholdValue)
                        ? $" · threshold {thresholdValue.GetDouble():0.00}" : "";
                    Message?.Invoke($"{ratio}epoch {epoch.GetInt32()}: validation Dice {dice}" +
                        $"{precision}{recall}{union}{threshold} {message}".TrimEnd());
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

    static bool ConfigurationMatches(string run, int minimumResolution, int inputSize,
        string negativeSelection)
    {
        string path = Path.Combine(run, "training-config.json");
        if (!File.Exists(path)) return minimumResolution == 0 && inputSize == 512 &&
            negativeSelection == "compare";
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            int revision = root.TryGetProperty("trainingRevision", out JsonElement revisionValue)
                ? revisionValue.GetInt32() : 1;
            int savedSize = root.TryGetProperty("inputSize", out JsonElement size) ? size.GetInt32() : 512;
            string savedNegatives = root.TryGetProperty("negativeSelection", out JsonElement negatives)
                ? negatives.GetString() ?? "compare" : "compare";
            return revision == TrainingRevision &&
                root.GetProperty("minimumResolution").GetInt32() == minimumResolution &&
                savedSize == inputSize && savedNegatives == negativeSelection;
        }
        catch { return false; }
    }

    static async Task PumpAsync(StreamReader source, StreamWriter destination)
    {
        while (await source.ReadLineAsync() is string line)
            await destination.WriteLineAsync(line);
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
        string modelId = manifest.RootElement.GetProperty("modelId").GetString()
            ?? throw new InvalidDataException("The package model ID is missing.");
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
