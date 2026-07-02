namespace DisplayLullaby;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("DisplayLullaby is a Windows-only tool.");
            return 1;
        }

        TryEnableDpiAwareness();

        try
        {
            if (args.Length == 0 || IsCommand(args[0], "tray"))
            {
                NativeMethods.FreeConsole();
                return TrayApplication.Run();
            }

            return RunCommand(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void TryEnableDpiAwareness()
    {
        try
        {
            if (NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2))
            {
                return;
            }

            if (NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAware))
            {
                return;
            }

            NativeMethods.SetProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
            NativeMethods.SetProcessDPIAware();
        }
    }

    private static int RunCommand(string[] args)
    {
        if (IsCommand(args[0], "list"))
        {
            return MonitorController.ListMonitors(Console.Out);
        }

        if (IsCommand(args[0], "config-path"))
        {
            Console.WriteLine(AppConfig.ConfigPath);
            return 0;
        }

        if (IsCommand(args[0], "sleep"))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Missing monitor target. Use a DISPLAY name such as DISPLAY1, a monitor ID, or 'all'.");
                return 2;
            }

            if (RejectRemovedRoleTarget(args[1]))
            {
                return 2;
            }

            return MonitorController.ApplyPowerMode(args[1], ParseSleepMode(args), Console.Out);
        }

        if (IsCommand(args[0], "toggle"))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Missing monitor target. Use a DISPLAY name such as DISPLAY1, a monitor ID, or 'all'.");
                return 2;
            }

            if (RejectRemovedRoleTarget(args[1]))
            {
                return 2;
            }

            return MonitorController.TogglePowerMode(args[1], ParseSleepMode(args), Console.Out);
        }

        if (IsCommand(args[0], "wake"))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Missing monitor target. Use a DISPLAY name such as DISPLAY1, a monitor ID, or 'all'.");
                return 2;
            }

            if (RejectRemovedRoleTarget(args[1]))
            {
                return 2;
            }

            return MonitorController.ApplyPowerMode(args[1], MonitorPowerMode.On, Console.Out);
        }

        if (IsCommand(args[0], "global-off") || IsCommand(args[0], "off"))
        {
            if (MonitorController.TrySendGlobalPowerOff())
            {
                Console.WriteLine("Sent Windows global monitor-off command.");
                return 0;
            }

            Console.Error.WriteLine($"Failed to send Windows global monitor-off command: {NativeMethods.LastErrorMessage()}");
            return 1;
        }

        PrintUsage(Console.Out);
        return IsCommand(args[0], "help") || args[0] is "-h" or "--help" or "/?" ? 0 : 2;
    }

    private static MonitorPowerMode ParseSleepMode(string[] args)
    {
        foreach (var arg in args.Skip(2))
        {
            if (arg.Equals("--standby", StringComparison.OrdinalIgnoreCase))
            {
                return MonitorPowerMode.Standby;
            }

            if (arg.Equals("--suspend", StringComparison.OrdinalIgnoreCase))
            {
                return MonitorPowerMode.Suspend;
            }

            if (arg.Equals("--power-off", StringComparison.OrdinalIgnoreCase))
            {
                return MonitorPowerMode.PowerOff;
            }

            if (arg.Equals("--soft-off", StringComparison.OrdinalIgnoreCase))
            {
                return MonitorPowerMode.SoftOff;
            }
        }

        return MonitorPowerMode.Standby;
    }

    private static bool RejectRemovedRoleTarget(string target)
    {
        if (!target.Equals("primary", StringComparison.OrdinalIgnoreCase) &&
            !target.Equals("secondary", StringComparison.OrdinalIgnoreCase) &&
            !target.Equals("non-primary", StringComparison.OrdinalIgnoreCase) &&
            !target.Equals("nonprimary", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Console.Error.WriteLine($"Target '{target}' is no longer supported. Use a DISPLAY name such as DISPLAY1.");
        return true;
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("DisplayLullaby");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  DisplayLullaby                 Run tray app");
        output.WriteLine("  DisplayLullaby tray            Run tray app");
        output.WriteLine("  DisplayLullaby list            List DDC/CI-capable physical monitors");
        output.WriteLine("  DisplayLullaby sleep <target>  Sleep DISPLAY name, monitor ID, or 'all'");
        output.WriteLine("  DisplayLullaby toggle <target> Toggle DISPLAY name, monitor ID, or 'all'");
        output.WriteLine("  DisplayLullaby wake <target>   Wake DISPLAY name, monitor ID, or 'all'");
        output.WriteLine("  DisplayLullaby global-off      Turn all monitors off using Windows");
        output.WriteLine("  DisplayLullaby config-path     Print tray hotkey config path");
        output.WriteLine();
        output.WriteLine("Sleep options:");
        output.WriteLine("  --standby                    Send DDC/CI power mode 0x02 (default)");
        output.WriteLine("  --power-off                  Send DDC/CI power mode 0x04");
        output.WriteLine("  --suspend                    Send DDC/CI power mode 0x03");
        output.WriteLine("  --soft-off                   Send DDC/CI power mode 0x05");
        output.WriteLine();
        output.WriteLine("Examples:");
        output.WriteLine("  DisplayLullaby list");
        output.WriteLine("  DisplayLullaby toggle DISPLAY2");
        output.WriteLine("  DisplayLullaby global-off");
        output.WriteLine("  DisplayLullaby sleep 2");
        output.WriteLine("  DisplayLullaby sleep all --standby");
        output.WriteLine("  DisplayLullaby wake 2");
    }

    private static bool IsCommand(string actual, string expected) =>
        actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
