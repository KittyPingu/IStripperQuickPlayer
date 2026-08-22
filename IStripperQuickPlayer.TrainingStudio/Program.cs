namespace IStripperQuickPlayer.TrainingStudio;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        int seedIndex = Array.FindIndex(args, value => string.Equals(value,
            "--seed-sealed-holdout", StringComparison.OrdinalIgnoreCase));
        if (seedIndex >= 0)
        {
            if (seedIndex + 1 >= args.Length)
                throw new ArgumentException(
                    "--seed-sealed-holdout requires the dataset directory.");
            DatasetStore store = new(args[seedIndex + 1]);
            (int sources, int frames, string backup) = store.SeedSealedHoldout();
            Console.WriteLine($"Seeded {frames} sealed frames from {sources} untouched videos. Backup: {backup}");
            return;
        }
        int topUpIndex = Array.FindIndex(args, value => string.Equals(value,
            "--top-up-sealed-holdout", StringComparison.OrdinalIgnoreCase));
        if (topUpIndex >= 0)
        {
            if (topUpIndex + 1 >= args.Length)
                throw new ArgumentException(
                    "--top-up-sealed-holdout requires the dataset directory.");
            DatasetStore store = new(args[topUpIndex + 1]);
            int created = store.TopUpSealedHoldout();
            Console.WriteLine($"Created {created} replacement sealed-holdout drafts.");
            return;
        }
        int repairIndex = Array.FindIndex(args, value => string.Equals(value,
            "--repair-sealed-holdout", StringComparison.OrdinalIgnoreCase));
        if (repairIndex >= 0)
        {
            if (repairIndex + 1 >= args.Length)
                throw new ArgumentException(
                    "--repair-sealed-holdout requires the dataset directory.");
            DatasetStore store = new(args[repairIndex + 1]);
            (int retired, int replacements, string? backup) = store.RepairSealedHoldout();
            Console.WriteLine($"Retired {retired} unusable sealed videos and selected " +
                $"{replacements} untouched replacements. Backup: {backup ?? "not needed"}");
            return;
        }
        if (args.Contains("--verify-training-studio", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = TrainingStudioVerification.Run() ? 0 : 1;
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrainingStudioForm());
    }
}
