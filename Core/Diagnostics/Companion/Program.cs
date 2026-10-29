using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MTTextClient.Core.Diagnostics.Companion;

/// <summary>Entrypoint for the explicitly configured read-only diagnostic companion.</summary>
internal static class Program
{
    private const int ConfigurationExitCode = 78;
    private const string Usage = "Usage: MTDiag.Companion --config <file> | --check-config <file>";
    private const string ConfigurationRejectedMessage = "Companion startup failed. Configuration or auth rejected (regular-file, symlink, owner-only permissions, or auth validation check). Run as the existing core.auth owner; the auth file must have owner-only permissions such as 0400 or 0600. Modes 0644 and 0664 are refused. Review ownership and access before changing permissions; no file was changed by this check.";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--help", StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync(Usage);
            return 0;
        }

        if (args.Length != 2 || (args[0] != "--config" && args[0] != "--check-config") || string.IsNullOrWhiteSpace(args[1]))
        {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        CompanionConfiguration configuration;
        try
        {
            configuration = CompanionConfiguration.Load(args[1]);
            if (args[0] == "--check-config")
            {
                foreach (CompanionProfileConfiguration profile in configuration.Profiles)
                {
                    _ = profile.CreateConnectionKeySeed();
                }
                await Console.Out.WriteLineAsync("Companion configuration and auth checks passed. No listeners started; network reachability and evidence availability were not checked.");
                return 0;
            }
        }
        catch (InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(ConfigurationRejectedMessage);
            return ConfigurationExitCode;
        }

        try
        {
            using var server = NativeCompanionServer.Create(configuration);
            using var cancellation = new CancellationTokenSource();
            using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
            using PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            await server.RunAsync(cancellation.Token);
            return 0;
        }
        catch (CompanionAuthConfigurationException)
        {
            await Console.Error.WriteLineAsync(ConfigurationRejectedMessage);
            return ConfigurationExitCode;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("Companion startup failed. Listener or native transport unavailable.");
            return 1;
        }
    }
}
