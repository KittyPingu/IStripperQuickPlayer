using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;

namespace IStripperQuickPlayer;

internal static class Sam3PipelineSupport
{
    internal const string Algorithm = "sam3-person";
    internal const double DefaultConfidence = .35;
    internal const string DefaultConcepts =
        "person = 0.35\r\nclothing = 0.35\r\nhand = 0.35\r\n" +
        "mouth = 0.35\r\nskin = 0.35\r\nleg = 0.35";
    internal const string DisplayName = "SAM3 foreground (optimized SAM2 + ViTMatte S)";
    internal static string RuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "sam2matting-sam3-worker", "v1");
    internal static string PythonExecutable => Path.Combine(RuntimeRoot,
        "venv", "Scripts", "python.exe");

    internal static (string[] Concepts, double[] Confidences) ParsePrompts(string text)
    {
        List<string> concepts = [];
        List<double> confidences = [];
        foreach (string entry in text.Split([',', ';', '\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = entry.IndexOf('=');
            string concept = separator < 0 ? entry : entry[..separator].Trim();
            double confidence = DefaultConfidence;
            if (separator >= 0 && !double.TryParse(entry[(separator + 1)..].Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out confidence))
                throw new InvalidDataException("Use concept = confidence, for example hand = 0.20.");
            int existing = concepts.FindIndex(value =>
                string.Equals(value, concept, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                if (confidences[existing] != confidence)
                    throw new InvalidDataException("Enter each concept with only one confidence value.");
                continue;
            }
            concepts.Add(concept);
            confidences.Add(confidence);
        }
        ValidateConcepts([.. concepts], [.. confidences]);
        return ([.. concepts], [.. confidences]);
    }

    internal static void ValidateConcepts(string[]? concepts, double[]? confidences = null)
    {
        if (concepts == null || concepts.Length is < 1 or > 16 ||
            concepts.Any(value => string.IsNullOrWhiteSpace(value) ||
                value.Length > 200 || value.Any(char.IsControl) ||
                value.IndexOfAny([',', ';']) >= 0))
            throw new InvalidDataException(
                "Enter 1–16 SAM3 concepts, separated by commas or new lines (up to 200 characters each).");
        if (confidences is { Length: > 0 } &&
            (confidences.Length != concepts.Length ||
                confidences.Any(value => !double.IsFinite(value) || value < 0 || value > 1)))
            throw new InvalidDataException("Each SAM3 confidence must be a number from 0 to 1, one per concept.");
    }

    internal static string FormatPrompts(string[] concepts, double[]? confidences = null)
    {
        ValidateConcepts(concepts, confidences);
        return string.Join(Environment.NewLine, concepts.Select((concept, index) =>
            concept + " = " + (confidences is { Length: > 0 }
                ? confidences[index] : DefaultConfidence).ToString("R", CultureInfo.InvariantCulture)));
    }

    internal static bool IsInstalled(CustomShowConfiguration configuration) =>
        File.Exists(PythonExecutable) &&
        File.Exists(CustomShowProcessor.Sam3PipelineWorkerPath) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "custom-shows", "sam3_tracking_repair.py")) &&
        File.Exists(Path.Combine(RuntimeRoot, "environment.json")) &&
        File.Exists(Path.Combine(RuntimeRoot, "source", "SAM2Matting", "sam3",
            "bpe_simple_vocab_16e6.txt.gz")) &&
        File.Exists(Path.Combine(RuntimeRoot, "checkpoints", "SAM2Matting-SAM3.pt")) &&
        new FileInfo(Path.Combine(RuntimeRoot, "checkpoints", "SAM2Matting-SAM3.pt"))
            .Length == 3509720141L &&
        CustomShowProcessor.IsViTMatteSmallInstalled(configuration) &&
        CustomShowProcessor.InstalledSam2Models(configuration).Contains("base-plus");

    internal static void ApplyRecipe(CustomShowProcessing processing, string[] concepts,
        double[]? confidences = null, bool selectiveBackwardRepair = true)
    {
        ValidateConcepts(concepts, confidences);
        processing.ForegroundConcepts = [.. concepts];
        processing.ForegroundConfidences = confidences is { Length: > 0 }
            ? [.. confidences] : Enumerable.Repeat(DefaultConfidence, concepts.Length).ToArray();
        processing.Sam3SelectiveBackwardRepair = selectiveBackwardRepair;
        processing.MattingDetailPx = 1024;
        processing.VitMatteInferenceDetailPx = 1024;
        processing.BatchSize = 3;
        processing.Sam2Model = "base-plus";
        processing.RvmInitializerAlphaThresholdPercent = null;
        processing.ExecutionPolicy = "eager";
        processing.PrecisionPolicy = "bf16-tracking-fp16-matting";
    }

    internal static bool VerifyConcepts()
    {
        var defaults = ParsePrompts(DefaultConcepts);
        if (!defaults.Concepts.SequenceEqual(["person", "clothing", "hand", "mouth", "skin", "leg"]) ||
            defaults.Confidences.Any(value => value != DefaultConfidence)) return false;
        var prompts = ParsePrompts(" person = 0.35, clothing\r\nPerson; red shoes = .20 ");
        if (!prompts.Concepts.SequenceEqual(["person", "clothing", "red shoes"]) ||
            !prompts.Confidences.SequenceEqual([.35, .35, .20]) ||
            !ParsePrompts(FormatPrompts(prompts.Concepts, prompts.Confidences))
                .Confidences.SequenceEqual(prompts.Confidences)) return false;
        foreach (string invalid in new[] { " , \n", new string('x', 201),
            string.Join(',', Enumerable.Range(0, 17)), "person\tclothing",
            "hand = -0.1", "hand = 1.1", "hand = NaN", "hand = Infinity",
            "hand = ", "= 0.2", "hand = 0.2\nHand = 0.35" })
        {
            try { ParsePrompts(invalid); return false; }
            catch (InvalidDataException) { }
        }
        return true;
    }
}
