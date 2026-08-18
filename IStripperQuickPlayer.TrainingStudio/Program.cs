namespace IStripperQuickPlayer.TrainingStudio;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--verify-training-studio", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = TrainingStudioVerification.Run() ? 0 : 1;
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrainingStudioForm());
    }
}
