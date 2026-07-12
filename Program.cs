using System;
using MTTextClient.Commands;
using MTTextClient.Core;
using MTTextClient.MCP;
using MTTextClient.Output;
namespace MTTextClient;

/// <summary>
/// MT Text Client — Multi-server text-first interface to MT-Core.
/// Connects to multiple MT-Core instances simultaneously.
/// Provides REPL for algorithm lifecycle management, account data, core monitoring,
/// server profile settings, V2 import, order/position management, and log analysis.
///
/// Supports four modes:
///   1) Interactive REPL (default)
///   2) Single-command  (pass command as CLI args)
///   3) MCP Server      (--mcp flag — stdio JSON-RPC for automation clients)
///   4) MCP Daemon      (--daemon &lt;unix-socket-path&gt; — one shared connection
///      stack over a Unix domain socket; many clients multiplex over it)
/// </summary>
public static class Program
{
    private static readonly ConnectionManager Manager = new();
    private static readonly OutputManager Output = new();
    private static readonly CommandRegistry Registry = new();

    public static void Main(string[] args)
    {
        // MCP server mode — run stdio JSON-RPC server
        if (args.Length > 0 && args[0] == "--mcp")
        {
            var server = new McpServer();
            server.Run();
            return;
        }

        // MCP daemon mode — one shared connection stack behind a Unix domain
        // socket; many clients multiplex over it. `--daemon <socket-path>`.
        // Require exactly one non-empty socket path; a malformed invocation
        // fails loudly (exit 2 + usage) rather than silently falling through
        // into ordinary command dispatch.
        if (args.Length > 0 && args[0] == "--daemon")
        {
            if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.Error.WriteLine("usage: MTTextClient --daemon <unix-socket-path>");
                Environment.Exit(2);
            }
            var server = new McpServer();
            server.RunSocket(args[1]);
            return;
        }

        PrintBanner();
        InitializeCommands();
        WireEvents();

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nUse 'exit' to quit.");
        };

        // Handle command-line arguments (non-interactive mode)
        if (args.Length > 0)
        {
            CommandResult result = Registry.Dispatch(string.Join(" ", args));
            Console.WriteLine(Output.Format(result));
            Manager.Dispose();
            return;
        }

        // Interactive REPL
        RunRepl();

        Manager.Dispose();
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  ╔══════════════════════════════════════════╗
  ║        MT Text Client v0.8.0             ║
  ║   Multi-server interface to MT-Core      ║
  ║   Algos + Orders + Monitor + MCP Server     ║
  ╚══════════════════════════════════════════╝
");
        Console.ResetColor();
        Console.WriteLine("Type 'help' for available commands, 'exit' to quit.\n");
    }

    private static void InitializeCommands()
    {
        // Connection management
        Registry.Register(new ConnectCommand(Manager));
        Registry.Register(new DisconnectCommand(Manager));
        Registry.Register(new UseCommand(Manager));
        Registry.Register(new StatusCommand(Manager));

        // Account data
        Registry.Register(new AccountCommand(Manager));
        Registry.Register(new CoreStatusCommand(Manager));
        Registry.Register(new ExchangeCommand(Manager));

        // Algorithm management (full lifecycle)
        Registry.Register(new AlgosCommand(Manager));

        // Server profile settings
        Registry.Register(new SettingsCommand(Manager));

        // V2 import + templates
        Registry.Register(new ImportCommand(Manager));

        // Orders & positions
        Registry.Register(new OrdersCommand(Manager));
        ReportStore ReportStore = new ReportStore();
        Registry.Register(new ReportsCommand(Manager, ReportStore));

        // Monitor — real-time core monitoring via UDP
        Registry.Register(new MonitorCommand(Manager));

        // Fleet management
        Registry.Register(new FleetCommand(Manager));

        // Risk Management & Observability
        Registry.Register(new AutoStopsCommand(Manager));
        Registry.Register(new BlacklistCommand(Manager));
        Registry.Register(new WhitelistCommand(Manager));
        Registry.Register(new ProfilesCommand());
        Registry.Register(new FoldersCommand());
        Registry.Register(new TPSLCommand(Manager));
        Registry.Register(new PerformanceCommand(Manager));
        Registry.Register(new NotificationsCommand(Manager));
        Registry.Register(new MarketDataCommand(Manager));
        Registry.Register(new AlertsCommand(Manager));
        Registry.Register(new ProfilingCommand(Manager));
        Registry.Register(new TriggersCommand(Manager));
        Registry.Register(new LiveMarketsCommand(Manager));
        Registry.Register(new AutoBuyCommand(Manager));
        Registry.Register(new GraphToolCommand(Manager));
        Registry.Register(new SignalsCommand(Manager));
        Registry.Register(new DustCommand(Manager));
        Registry.Register(new DepositCommand(Manager));
        Registry.Register(new FundingCommand(Manager));
        Registry.Register(new BuyApiLimitCommand(Manager));

        // Configuration
        Registry.Register(new ProfileCommand());
        Registry.Register(new OutputCommand(Output));
        Registry.Register(new HelpCommand(Registry));
    }

    private static void WireEvents()
    {
        Manager.OnConnectionEstablished += conn =>
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[CONNECTED] {conn.Name} — {conn.Profile.Address}:{conn.Profile.Port} ({conn.Profile.Exchange})");
            Console.ResetColor();
            PrintPrompt();
        };

        Manager.OnConnectionLost += conn =>
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n[DISCONNECTED] {conn.Name}");
            Console.ResetColor();
            PrintPrompt();
        };

        Manager.OnConnectionError += (conn, msg) =>
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] {msg}");
            Console.ResetColor();
        };

        Manager.OnAlgorithmsLoaded += (conn, count) =>
        {
            (int running, int stopped, int _) = conn.AlgoStore.GetCounts();
            int groupCount = conn.AlgoStore.GroupCount;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n[SYNC] {conn.Name}: {count} algorithm(s) ({running} running), {groupCount} group(s)");
            Console.ResetColor();
            PrintPrompt();
        };

        // Data-stream events
        Manager.OnCoreStatusReceived += conn =>
        {
            CoreLicenseSnapshot? license = conn.CoreStatusStore.GetLicense();
            if (license != null && license.Timestamp > DateTime.UtcNow.AddSeconds(-2))
            {
                // Only print on initial license receipt (not every metrics update)
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n[SYNC] {conn.Name}: Core v{license.BuildVersion} ({license.CoreOS}) | License: {license.LicenseName}");
                Console.ResetColor();
                PrintPrompt();
            }
        };

        Manager.OnTradePairsLoaded += (conn, count) =>
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n[SYNC] {conn.Name}: {count} trade pair(s) loaded");
            Console.ResetColor();
            PrintPrompt();
        };

        Manager.OnAccountDataReceived += conn =>
        {
            AccountInfoSnapshot? info = conn.AccountStore.GetAccountInfo();
            if (info != null && info.Timestamp > DateTime.UtcNow.AddSeconds(-2))
            {
                int balCount = conn.AccountStore.BalanceCount;
                int posCount = conn.AccountStore.OpenPositionCount;
                int ordCount = conn.AccountStore.ActiveOrderCount;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n[SYNC] {conn.Name}: Account data — {balCount} balance(s), {posCount} position(s), {ordCount} order(s)");
                Console.ResetColor();
                PrintPrompt();
            }
        };
    }

    private static void RunRepl()
    {
        while (true)
        {
            PrintPrompt();
            string? input = Console.ReadLine();

            if (input == null) // EOF
            {
                break;
            }

            input = input.Trim();

            if (string.IsNullOrEmpty(input))
            {
                continue;
            }

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Disconnecting all...");
                Manager.DisconnectAll();
                Console.WriteLine("Goodbye.");
                break;
            }

            CommandResult result = Registry.Dispatch(input);
            string? formatted = Output.Format(result);

            Console.ForegroundColor = result.Success ? ConsoleColor.White : ConsoleColor.Red;
            Console.WriteLine(formatted);
            Console.ResetColor();
        }
    }

    private static void PrintPrompt()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        int connCount = Manager.ConnectedCount;
        string? active = Manager.ActiveConnectionName;

        if (connCount > 1)
        {
            Console.Write($"mt[{active}|{connCount}]> ");
        }
        else if (connCount == 1)
        {
            Console.Write($"mt[{active}]> ");
        }
        else
        {
            Console.Write("mt> ");
        }

        Console.ResetColor();
    }
}
