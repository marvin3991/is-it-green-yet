namespace IsItGreenYet.Cli;

internal static class Program
{
    /// <summary>只把參數與主控台交給 <see cref="CliApp.RunAsync"/>；未預期的例外在那裡接住（第二階段 spec §10.6 第 2 列）。</summary>
    private static Task<int> Main(string[] args) =>
        CliApp.RunAsync(args, Console.Out, Console.Error, CliEnvironment.CreateDefault(), CancellationToken.None);
}
