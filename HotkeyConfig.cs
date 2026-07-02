namespace DisplayLullaby;

internal enum TrayAction
{
    Sleep,
    Wake,
    Toggle,
    GlobalOff,
    TemporaryStandby
}

internal sealed record HotkeyBinding(
    int Id,
    NativeMethods.HotkeyModifiers Modifiers,
    uint VirtualKey,
    TrayAction Action,
    string Target,
    string DisplayText);

internal sealed record AppSettings(
    string GlobalOffHotkey,
    string PrimaryStandbyHotkey,
    string PrimaryStandbyTarget,
    string SecondaryStandbyHotkey,
    string SecondaryStandbyTarget,
    MonitorPowerMode PowerMode,
    int TemporaryStandbyIdleMinutes,
    int TemporaryStandbyWakeDelaySeconds);

internal sealed class AppConfig
{
    private const string AppDataFolderName = "DisplayLullaby";
    private const string LegacyAppDataFolderName = "MonitorSleep";
    private const string ConfigFileName = "config.ini";

    private const string DefaultConfig = """
        # DisplayLullaby tray configuration
        #
        # PowerMode can be Standby, Suspend, PowerOff, or SoftOff.
        # Standby is the default because more monitors can wake from it.
        PowerMode=Standby

        # Format: Hotkey=<keys> <global-off|sleep|wake|toggle|temporary-standby> <target>
        # global-off ignores target and turns all monitors off using Windows.
        Hotkey=F9 global-off all

        # temporary-standby uses DDC/CI only as a short-term per-monitor standby.
        # Press the same hotkey again, or press any key / move the mouse after a brief
        # grace period, to wake the monitor and cancel the automatic handoff.
        # After this many minutes without keyboard/mouse input, DisplayLullaby wakes
        # the DDC monitor, waits briefly, then uses the Windows global monitor-off command.
        # Set TemporaryStandbyIdleMinutes=0 to disable the automatic handoff.
        TemporaryStandbyIdleMinutes=15
        TemporaryStandbyWakeDelaySeconds=4
        Hotkey=F10 temporary-standby DISPLAY1
        Hotkey=F11 temporary-standby DISPLAY2
        """;

    private AppConfig(
        MonitorPowerMode sleepMode,
        int temporaryStandbyIdleMinutes,
        int temporaryStandbyWakeDelaySeconds,
        IReadOnlyList<HotkeyBinding> hotkeys,
        IReadOnlyList<string> warnings)
    {
        SleepMode = sleepMode;
        TemporaryStandbyIdleMinutes = temporaryStandbyIdleMinutes;
        TemporaryStandbyWakeDelaySeconds = temporaryStandbyWakeDelaySeconds;
        Hotkeys = hotkeys;
        Warnings = warnings;
    }

    public MonitorPowerMode SleepMode { get; }

    public int TemporaryStandbyIdleMinutes { get; }

    public int TemporaryStandbyWakeDelaySeconds { get; }

    public IReadOnlyList<HotkeyBinding> Hotkeys { get; }

    public IReadOnlyList<string> Warnings { get; }

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataFolderName, ConfigFileName);

    private static string LegacyConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyAppDataFolderName, ConfigFileName);

    public static AppConfig LoadOrCreate()
    {
        var path = ConfigPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(path))
        {
            WriteInitialConfig(path);
        }

        var migrated = MigrateRemovedRoleTargets(path);
        var config = Parse(File.ReadAllLines(path));
        if (migrated || ContainsRemovedRoleWording(File.ReadAllText(path)))
        {
            SaveSettings(config.ToSettings());
            config = Parse(File.ReadAllLines(path));
        }

        return config;
    }

    public AppSettings ToSettings()
    {
        var global = Hotkeys.FirstOrDefault(static hotkey => hotkey.Action == TrayAction.GlobalOff);
        var temporaryHotkeys = Hotkeys
            .Where(static hotkey => hotkey.Action == TrayAction.TemporaryStandby)
            .ToArray();

        var primary = temporaryHotkeys.FirstOrDefault(static hotkey => hotkey.Target.Equals("DISPLAY1", StringComparison.OrdinalIgnoreCase))
                      ?? temporaryHotkeys.FirstOrDefault();
        var secondary = temporaryHotkeys.FirstOrDefault(static hotkey => hotkey.Target.Equals("DISPLAY2", StringComparison.OrdinalIgnoreCase))
                        ?? temporaryHotkeys.FirstOrDefault(hotkey => !ReferenceEquals(hotkey, primary));

        return new AppSettings(
            ExtractHotkeyText(global, "F9"),
            ExtractHotkeyText(primary, "F10"),
            NormalizeRemovedRoleTarget(string.IsNullOrWhiteSpace(primary?.Target) ? "DISPLAY1" : primary.Target),
            ExtractHotkeyText(secondary, "F11"),
            NormalizeRemovedRoleTarget(string.IsNullOrWhiteSpace(secondary?.Target) ? "DISPLAY2" : secondary.Target),
            SleepMode,
            TemporaryStandbyIdleMinutes,
            TemporaryStandbyWakeDelaySeconds);
    }

    public static void SaveSettings(AppSettings settings)
    {
        var path = ConfigPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, BuildConfig(settings));
    }

    public static bool TryValidateSettings(AppSettings settings, out string warning)
    {
        if (!TryValidateHotkeyText(settings.GlobalOffHotkey, out warning))
        {
            warning = $"All monitors off hotkey: {warning}";
            return false;
        }

        if (!TryValidateHotkeyText(settings.PrimaryStandbyHotkey, out warning))
        {
            warning = $"F10 standby hotkey: {warning}";
            return false;
        }

        if (!TryValidateHotkeyText(settings.SecondaryStandbyHotkey, out warning))
        {
            warning = $"F11 standby hotkey: {warning}";
            return false;
        }

        if (!TryValidateTargetText(settings.PrimaryStandbyTarget, out warning))
        {
            warning = $"F10 standby target: {warning}";
            return false;
        }

        if (!TryValidateTargetText(settings.SecondaryStandbyTarget, out warning))
        {
            warning = $"F11 standby target: {warning}";
            return false;
        }

        if (settings.PrimaryStandbyTarget.Trim().Equals(settings.SecondaryStandbyTarget.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warning = "Primary and secondary standby targets must be different.";
            return false;
        }

        if (settings.TemporaryStandbyIdleMinutes is < 0 or > 1440)
        {
            warning = "Idle handoff minutes must be between 0 and 1440.";
            return false;
        }

        if (settings.TemporaryStandbyWakeDelaySeconds is < 0 or > 60)
        {
            warning = "Wake delay seconds must be between 0 and 60.";
            return false;
        }

        warning = string.Empty;
        return true;
    }

    public static bool TryValidateHotkeyText(string text, out string warning)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            warning = "hotkey cannot be empty.";
            return false;
        }

        return TryParseKeyChord(text.Trim(), out _, out _, out warning);
    }

    private static void WriteInitialConfig(string path)
    {
        var legacyPath = LegacyConfigPath;
        if (File.Exists(legacyPath))
        {
            File.WriteAllText(path, MigrateLegacyConfig(File.ReadAllText(legacyPath)));
            return;
        }

        File.WriteAllText(path, DefaultConfig);
    }

    private static string MigrateLegacyConfig(string configText) =>
        configText.Replace("MonitorSleep", "DisplayLullaby", StringComparison.Ordinal);

    private static bool MigrateRemovedRoleTargets(string path)
    {
        var original = File.ReadAllText(path);
        var primaryTarget = GetDetectedDisplayTarget(primary: true, "DISPLAY1");
        var secondaryTarget = GetDetectedDisplayTarget(primary: false, "DISPLAY2");
        var migratedLines = original
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => MigrateRemovedRoleTargetLine(line, primaryTarget, secondaryTarget));
        var migrated = string.Join(Environment.NewLine, migratedLines);

        if (!migrated.Equals(original, StringComparison.Ordinal))
        {
            File.WriteAllText(path, migrated);
            return true;
        }

        return false;
    }

    private static bool ContainsRemovedRoleWording(string configText) =>
        configText.Contains("current primary monitor", StringComparison.OrdinalIgnoreCase) ||
        configText.Contains("current secondary monitor", StringComparison.OrdinalIgnoreCase) ||
        configText.Contains("primary display", StringComparison.OrdinalIgnoreCase) ||
        configText.Contains("secondary display", StringComparison.OrdinalIgnoreCase);

    private static string MigrateRemovedRoleTargetLine(string line, string primaryTarget, string secondaryTarget)
    {
        var commentIndex = line.IndexOf('#');
        var active = commentIndex < 0 ? line : line[..commentIndex];
        var comment = commentIndex < 0 ? string.Empty : line[commentIndex..];
        var splitAt = active.IndexOf('=');
        if (splitAt < 0 || !active[..splitAt].Trim().Equals("Hotkey", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        var value = active[(splitAt + 1)..];
        var leadingWhitespace = value.Length - value.TrimStart().Length;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !TryMapRemovedRoleTarget(parts[2], primaryTarget, secondaryTarget, out var replacement))
        {
            return line;
        }

        return string.Concat(
            active[..(splitAt + 1)],
            new string(' ', leadingWhitespace),
            parts[0],
            " ",
            parts[1],
            " ",
            replacement,
            comment);
    }

    private static string GetDetectedDisplayTarget(bool primary, string fallback) =>
        MonitorController.TryGetRoleDisplayName(primary, out var displayName) ? displayName : fallback;

    private static string NormalizeRemovedRoleTarget(string target)
    {
        if (TryMapRemovedRoleTarget(target, "DISPLAY1", "DISPLAY2", out var replacement))
        {
            return replacement;
        }

        return target;
    }

    private static bool TryMapRemovedRoleTarget(string target, string primaryReplacement, string secondaryReplacement, out string replacement)
    {
        if (target.Equals("primary", StringComparison.OrdinalIgnoreCase))
        {
            replacement = primaryReplacement;
            return true;
        }

        if (target.Equals("secondary", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("non-primary", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("nonprimary", StringComparison.OrdinalIgnoreCase))
        {
            replacement = secondaryReplacement;
            return true;
        }

        replacement = string.Empty;
        return false;
    }

    private static string BuildConfig(AppSettings settings)
    {
        var primaryTarget = NormalizeConfigToken(settings.PrimaryStandbyTarget, "DISPLAY1");
        var secondaryTarget = NormalizeConfigToken(settings.SecondaryStandbyTarget, "DISPLAY2");

        return $"""
            # DisplayLullaby tray configuration
            #
            # F9 uses the Windows global monitor-off command.
            # F10 uses DDC/CI only as a temporary standby/wake toggle for {primaryTarget}.
            # F11 uses DDC/CI only as a temporary standby/wake toggle for {secondaryTarget}.
            # While temporary standby is active, the same hotkey again or any later keyboard/mouse input wakes the monitor and cancels handoff.
            # After the configured idle period, DisplayLullaby wakes that monitor, waits briefly, then uses F9-style global monitor-off.
            PowerMode={SerializePowerMode(settings.PowerMode)}
            TemporaryStandbyIdleMinutes={settings.TemporaryStandbyIdleMinutes}
            TemporaryStandbyWakeDelaySeconds={settings.TemporaryStandbyWakeDelaySeconds}
            Hotkey={settings.GlobalOffHotkey.Trim()} global-off all
            Hotkey={settings.PrimaryStandbyHotkey.Trim()} temporary-standby {primaryTarget}
            Hotkey={settings.SecondaryStandbyHotkey.Trim()} temporary-standby {secondaryTarget}
            """;
    }

    private static string ExtractHotkeyText(HotkeyBinding? binding, string fallback)
    {
        if (binding is null)
        {
            return fallback;
        }

        var parts = binding.DisplayText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? fallback : parts[0];
    }

    private static string NormalizeConfigToken(string value, string fallback)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? fallback
            : trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    private static bool TryValidateTargetText(string text, out string warning)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            warning = "target cannot be empty.";
            return false;
        }

        if (TryMapRemovedRoleTarget(trimmed, "DISPLAY1", "DISPLAY2", out _))
        {
            warning = $"'{trimmed}' is no longer supported; use a DISPLAY name such as DISPLAY1.";
            return false;
        }

        warning = string.Empty;
        return true;
    }

    private static string SerializePowerMode(MonitorPowerMode powerMode) =>
        powerMode switch
        {
            MonitorPowerMode.Standby => "Standby",
            MonitorPowerMode.Suspend => "Suspend",
            MonitorPowerMode.PowerOff => "PowerOff",
            MonitorPowerMode.SoftOff => "SoftOff",
            _ => "Standby"
        };

    public static AppConfig Parse(IEnumerable<string> lines)
    {
        var sleepMode = MonitorPowerMode.Standby;
        var temporaryStandbyIdleMinutes = 15;
        var temporaryStandbyWakeDelaySeconds = 4;
        var hotkeys = new List<HotkeyBinding>();
        var warnings = new List<string>();
        var nextHotkeyId = 100;
        var lineNumber = 0;

        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var splitAt = line.IndexOf('=');
            if (splitAt < 0)
            {
                warnings.Add($"Line {lineNumber}: expected Name=Value.");
                continue;
            }

            var name = line[..splitAt].Trim();
            var value = line[(splitAt + 1)..].Trim();
            if (name.Equals("PowerMode", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParsePowerMode(value, out var parsedMode))
                {
                    sleepMode = parsedMode;
                }
                else
                {
                    warnings.Add($"Line {lineNumber}: unknown PowerMode '{value}'.");
                }
            }
            else if (name.Equals("TemporaryStandbyIdleMinutes", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("DdcHandoffIdleMinutes", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseIntSetting(value, 0, 1440, out var parsedMinutes))
                {
                    temporaryStandbyIdleMinutes = parsedMinutes;
                }
                else
                {
                    warnings.Add($"Line {lineNumber}: TemporaryStandbyIdleMinutes must be between 0 and 1440.");
                }
            }
            else if (name.Equals("TemporaryStandbyWakeDelaySeconds", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("DdcWakeDelaySeconds", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseIntSetting(value, 0, 60, out var parsedSeconds))
                {
                    temporaryStandbyWakeDelaySeconds = parsedSeconds;
                }
                else
                {
                    warnings.Add($"Line {lineNumber}: TemporaryStandbyWakeDelaySeconds must be between 0 and 60.");
                }
            }
            else if (name.Equals("Hotkey", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHotkey(value, nextHotkeyId, out var hotkey, out var warning))
                {
                    hotkeys.Add(hotkey);
                    nextHotkeyId++;
                }
                else
                {
                    warnings.Add($"Line {lineNumber}: {warning}");
                }
            }
            else
            {
                warnings.Add($"Line {lineNumber}: unknown setting '{name}'.");
            }
        }

        return new AppConfig(sleepMode, temporaryStandbyIdleMinutes, temporaryStandbyWakeDelaySeconds, hotkeys, warnings);
    }

    private static bool TryParsePowerMode(string text, out MonitorPowerMode powerMode)
    {
        if (text.Equals("SoftOff", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Off", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("5", StringComparison.OrdinalIgnoreCase))
        {
            powerMode = MonitorPowerMode.SoftOff;
            return true;
        }

        if (text.Equals("Standby", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("2", StringComparison.OrdinalIgnoreCase))
        {
            powerMode = MonitorPowerMode.Standby;
            return true;
        }

        if (text.Equals("Suspend", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("3", StringComparison.OrdinalIgnoreCase))
        {
            powerMode = MonitorPowerMode.Suspend;
            return true;
        }

        if (text.Equals("PowerOff", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Power-Off", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("4", StringComparison.OrdinalIgnoreCase))
        {
            powerMode = MonitorPowerMode.PowerOff;
            return true;
        }

        powerMode = MonitorPowerMode.SoftOff;
        return false;
    }

    private static bool TryParseHotkey(string value, int id, out HotkeyBinding binding, out string warning)
    {
        binding = null!;
        warning = string.Empty;

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            warning = "expected '<keys> <global-off|sleep|wake|toggle|temporary-standby> <target>'.";
            return false;
        }

        if (!TryParseKeyChord(parts[0], out var modifiers, out var virtualKey, out warning))
        {
            return false;
        }

        if (!TryParseAction(parts[1], out var action))
        {
            warning = $"unknown action '{parts[1]}'.";
            return false;
        }

        var target = parts[2];
        if (!TryValidateTargetText(target, out warning))
        {
            return false;
        }

        binding = new HotkeyBinding(id, modifiers, virtualKey, action, target, value);
        return true;
    }

    private static bool TryParseAction(string value, out TrayAction action)
    {
        if (value.Equals("sleep", StringComparison.OrdinalIgnoreCase))
        {
            action = TrayAction.Sleep;
            return true;
        }

        if (value.Equals("wake", StringComparison.OrdinalIgnoreCase))
        {
            action = TrayAction.Wake;
            return true;
        }

        if (value.Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            action = TrayAction.Toggle;
            return true;
        }

        if (value.Equals("temporary-standby", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("temp-standby", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("ddc-standby", StringComparison.OrdinalIgnoreCase))
        {
            action = TrayAction.TemporaryStandby;
            return true;
        }

        if (value.Equals("global-off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("monitor-off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("monitors-off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            action = TrayAction.GlobalOff;
            return true;
        }

        action = TrayAction.Sleep;
        return false;
    }

    private static bool TryParseIntSetting(string text, int minValue, int maxValue, out int value)
    {
        if (int.TryParse(text, out value) && value >= minValue && value <= maxValue)
        {
            return true;
        }

        value = minValue;
        return false;
    }

    private static bool TryParseKeyChord(string text, out NativeMethods.HotkeyModifiers modifiers, out uint virtualKey, out string warning)
    {
        modifiers = NativeMethods.HotkeyModifiers.NoRepeat;
        virtualKey = 0;
        warning = string.Empty;

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            warning = "empty hotkey.";
            return false;
        }

        foreach (var token in tokens[..^1])
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.HotkeyModifiers.Control;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.HotkeyModifiers.Alt;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.HotkeyModifiers.Shift;
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.HotkeyModifiers.Windows;
            }
            else
            {
                warning = $"unknown modifier '{token}'.";
                return false;
            }
        }

        var key = tokens[^1];
        if (!TryParseVirtualKey(key, out virtualKey))
        {
            warning = $"unknown key '{key}'.";
            return false;
        }

        return true;
    }

    private static bool TryParseVirtualKey(string key, out uint virtualKey)
    {
        virtualKey = 0;

        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
        }

        if (key.StartsWith('F') && int.TryParse(key[1..], out var functionKey) && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }

        if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key[6..], out var numpadKey) &&
            numpadKey is >= 0 and <= 9)
        {
            virtualKey = (uint)(0x60 + numpadKey);
            return true;
        }

        virtualKey = key.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            "RETURN" => 0x0D,
            "BACKSPACE" => 0x08,
            "ESC" => 0x1B,
            "ESCAPE" => 0x1B,
            "PAUSE" => 0x13,
            "SCROLLLOCK" => 0x91,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            _ => 0
        };

        return virtualKey != 0;
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf('#');
        return index < 0 ? line : line[..index];
    }
}
