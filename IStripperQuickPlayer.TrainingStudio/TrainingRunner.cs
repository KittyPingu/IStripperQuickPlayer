using System.Diagnostics;
using System.Text.Json;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class TrainingRunner
{
    Process? process;
    internal event Action<string>? Message;
    internal string? PackagePath { get; private set; }
    internal string? ReviewPath { get; private set; }

    internal async Task<int> RunAsync(string datasetRoot, bool resume, CancellationToken token)
    {
        string runs = Path.Combine(datasetRoot, "runs");
        string? resumable = resume && Directory.Exists(runs)
            ? Directory.EnumerateDirectories(runs)
                .Where(directory => File.Exists(Path.Combine(directory, "last.pth")))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        string output = resumable ?? Path.Combine(runs, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        if (resume && resumable == null) Message?.Invoke("No resumable checkpoint was found; starting a new run.");
        else if (resumable != null) Message?.Invoke($"Resuming {Path.GetFileName(resumable)} from its last checkpoint.");
        string worker = Path.Combine(AppContext.BaseDirectory, "training", "prop_segmenter_train.py");
        ProcessStartInfo start = new(Sam2Client.RuntimePython())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { worker, "--dataset", datasetRoot, "--output", output })
            start.ArgumentList.Add(argument);
        if (resumable != null) start.ArgumentList.Add("--resume");
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the training worker.");
        Task<string> errors = process.StandardError.ReadToEndAsync(token);
        while (await process.StandardOutput.ReadLineAsync(token) is string line)
        {
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
                    Message?.Invoke($"Epoch {epoch.GetInt32()}: validation Dice {dice} {message}".Trim());
                }
                else Message?.Invoke(($"{stage}: {message}").Trim(' ', ':'));
            }
            catch { Message?.Invoke(line); }
        }
        await process.WaitForExitAsync(token);
        string error = await errors;
        if (process.ExitCode != 0) Message?.Invoke(error.Trim());
        return process.ExitCode;
    }

    internal void Cancel()
    {
        try { if (process is { HasExited: false }) process.Kill(true); } catch { }
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
