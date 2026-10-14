using DisplayLullaby.SessionRestore;
using Xunit;

namespace DisplayLullaby.Tests;

public sealed class SnapshotSerializerTests
{
    [Fact]
    public void RoundTripsSnapshot()
    {
        var snapshot = TestData.Snapshot(TestData.App("notepad", @"C:\Windows\notepad.exe"));
        var json = SnapshotSerializer.Serialize(snapshot);

        Assert.Contains("\"maximized\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(SnapshotSerializer.TryDeserialize(json, out var back));
        Assert.Equal(snapshot.CapturedAt, back!.CapturedAt);
        Assert.Equal("notepad", back.Apps[0].ProcessName);
        Assert.Equal(new WindowRect(10, 20, 810, 620), back.Apps[0].Windows[0].NormalBounds);
        Assert.Equal(WindowShowState.Maximized, back.Apps[0].Windows[0].ShowState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"version\":99,\"capturedAt\":\"2026-01-01T00:00:00Z\",\"machineName\":\"x\",\"reason\":\"r\",\"apps\":[]}")]
    public void RejectsInvalidOrFutureSnapshots(string json) =>
        Assert.False(SnapshotSerializer.TryDeserialize(json, out _));
}

public sealed class SnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dl-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void KeepsOnlyNewestSnapshotsAndLoadsLatest()
    {
        var store = new SnapshotStore(_dir, keepCount: 3);
        var start = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 6; i++)
        {
            store.Save(TestData.Snapshot(start.AddMinutes(i), TestData.App($"app{i}", null)));
        }

        Assert.Equal(3, store.ListSnapshotFiles().Count);
        Assert.Equal("app5", store.LoadLatest()!.Apps[0].ProcessName);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void SkipsCorruptNewestFile()
    {
        var store = new SnapshotStore(_dir);
        store.Save(TestData.Snapshot(TestData.App("good", null)));
        File.WriteAllText(Path.Combine(_dir, "session-99991231-235959-999.json"), "{ truncated");

        Assert.Equal("good", store.LoadLatest()!.Apps[0].ProcessName);
    }

    [Fact]
    public void LoadLatestReturnsNullWhenEmpty() =>
        Assert.Null(new SnapshotStore(_dir).LoadLatest());
}

public sealed class IgnoreMatcherTests
{
    [Theory]
    [InlineData("notepad", "notepad", null, null, true)]
    [InlineData("notepad.exe", "Notepad", null, null, true)]
    [InlineData("NOTEPAD", "notepad", null, null, true)]
    [InlineData("C:\\Tools\\*", "foo", "C:\\Tools\\foo.exe", null, true)]
    [InlineData("*.Photos_*", "Photos", null, "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", true)]
    [InlineData("code?", "code", null, null, false)]
    [InlineData("calc", "calculator", null, null, false)]
    public void Matches(string pattern, string process, string? exe, string? aumid, bool expected) =>
        Assert.Equal(expected, new IgnoreMatcher([pattern]).IsIgnored(process, exe, aumid));

    [Fact]
    public void BlankPatternsIgnored() =>
        Assert.False(new IgnoreMatcher(["", "  "]).IsIgnored("anything", null, null));
}

public sealed class RestorePlannerTests
{
    private static readonly IgnoreMatcher NoIgnore = new([]);

    [Fact]
    public void LaunchesMissingAndPlacesRunning()
    {
        var snapshot = TestData.Snapshot(
            TestData.App("notepad", @"C:\Windows\notepad.exe"),
            TestData.App("mspaint", @"C:\Windows\mspaint.exe"));
        var running = new[] { new RunningProcessInfo("notepad", @"c:\windows\NOTEPAD.EXE", null) };

        var plan = RestorePlanner.Plan(snapshot, running, NoIgnore, [], "DisplayLullaby");

        Assert.Equal(RestoreAction.PlaceOnly, plan[0].Action);
        Assert.Equal(RestoreAction.LaunchAndPlace, plan[1].Action);
    }

    [Fact]
    public void SkipsIgnoredSelfAndUnlaunchable()
    {
        var snapshot = TestData.Snapshot(
            TestData.App("DisplayLullaby", @"C:\x\DisplayLullaby.exe"),
            TestData.App("Taskmgr", @"C:\Windows\Taskmgr.exe"),
            TestData.App("elevated", null));

        var plan = RestorePlanner.Plan(snapshot, [], new IgnoreMatcher(["taskmgr"]), [], "DisplayLullaby");

        Assert.All(plan, item => Assert.Equal(RestoreAction.Skip, item.Action));
    }

    [Fact]
    public void SelfRestoringAppLaunchedOnce()
    {
        var snapshot = TestData.Snapshot(
            TestData.App("chrome", @"C:\chrome.exe", "\"C:\\chrome.exe\""),
            TestData.App("chrome", @"C:\chrome.exe", "\"C:\\chrome.exe\" --profile-directory=Work"));

        var plan = RestorePlanner.Plan(snapshot, [], NoIgnore, ["chrome"], "DisplayLullaby");

        Assert.Equal(RestoreAction.LaunchAndPlace, plan[0].Action);
        Assert.Equal(RestoreAction.PlaceOnly, plan[1].Action);
    }

    [Fact]
    public void DistinctInstancesOfNormalAppBothLaunch()
    {
        var snapshot = TestData.Snapshot(
            TestData.App("notepad", @"C:\notepad.exe", "notepad a.txt"),
            TestData.App("notepad", @"C:\notepad.exe", "notepad b.txt"),
            TestData.App("notepad", @"C:\notepad.exe", "notepad b.txt"));

        var plan = RestorePlanner.Plan(snapshot, [], NoIgnore, [], "DisplayLullaby");

        Assert.Equal([RestoreAction.LaunchAndPlace, RestoreAction.LaunchAndPlace, RestoreAction.Skip], plan.Select(p => p.Action));
    }

    [Fact]
    public void PackagedAppsDedupeByAumid()
    {
        var app = TestData.App("Calculator", @"C:\Program Files\WindowsApps\calc.exe") with
        {
            AppUserModelId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            IsPackaged = true
        };
        var running = new[] { new RunningProcessInfo("CalculatorApp", @"C:\other.exe", "microsoft.windowscalculator_8wekyb3d8bbwe!app") };

        var plan = RestorePlanner.Plan(TestData.Snapshot(app), running, NoIgnore, [], "DisplayLullaby");

        Assert.Equal(RestoreAction.PlaceOnly, plan[0].Action);
    }

    [Fact]
    public void LooksAlreadyRestoredWhenMostAppsRun()
    {
        var snapshot = TestData.Snapshot(TestData.App("a", "a.exe"), TestData.App("b", "b.exe"), TestData.App("c", "c.exe"));
        var running = new[] { new RunningProcessInfo("a", "a.exe", null), new RunningProcessInfo("b", "b.exe", null) };

        var plan = RestorePlanner.Plan(snapshot, running, NoIgnore, [], "x");

        Assert.True(RestorePlanner.LooksAlreadyRestored(plan));
        Assert.False(RestorePlanner.LooksAlreadyRestored(RestorePlanner.Plan(snapshot, [], NoIgnore, [], "x")));
    }

    [Fact]
    public void AutoRestoreOnlyAfterReboot()
    {
        var now = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var boot = now.AddMinutes(-2);
        var beforeBoot = TestData.Snapshot(boot.AddMinutes(-5), TestData.App("a", "a.exe"));
        var afterBoot = TestData.Snapshot(boot.AddMinutes(1), TestData.App("a", "a.exe"));
        var stale = TestData.Snapshot(now.AddDays(-30), TestData.App("a", "a.exe"));

        Assert.True(RestoreTrigger.ShouldAutoRestore(beforeBoot, boot, now, TimeSpan.FromDays(7)));
        Assert.False(RestoreTrigger.ShouldAutoRestore(afterBoot, boot, now, TimeSpan.FromDays(7)));
        Assert.False(RestoreTrigger.ShouldAutoRestore(stale, boot, now, TimeSpan.FromDays(7)));
        Assert.False(RestoreTrigger.ShouldAutoRestore(null, boot, now, TimeSpan.FromDays(7)));
    }
}

public sealed class SettingsAndHelpersTests
{
    [Fact]
    public void DefaultFileParsesWithoutWarnings()
    {
        var settings = SessionRestoreSettings.Parse(SessionRestoreSettings.DefaultFileText.Split('\n'));

        Assert.Empty(settings.Warnings);
        Assert.Equal(3, settings.SnapshotIntervalMinutes);
        Assert.Contains("explorer", settings.Ignore);
        Assert.Contains("chrome", settings.SelfRestoring);
    }

    [Fact]
    public void InvalidValuesWarnAndKeepDefaults()
    {
        var settings = SessionRestoreSettings.Parse(["SnapshotIntervalMinutes=0", "Enabled=maybe", "Bogus=1", "Ignore=foo # comment"]);

        Assert.Equal(3, settings.SnapshotIntervalMinutes);
        Assert.True(settings.Enabled);
        Assert.Equal(3, settings.Warnings.Count);
        Assert.Equal(["foo"], settings.Ignore);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --flag x", null, "C:\\Program Files\\App\\app.exe", "--flag x")]
    [InlineData("notepad.exe  file.txt", "C:\\Windows\\notepad.exe", "C:\\Windows\\notepad.exe", "file.txt")]
    [InlineData("", "C:\\a.exe", "C:\\a.exe", "")]
    public void SplitsCommandLine(string commandLine, string? exe, string expectedFile, string expectedArgs)
    {
        var (file, args) = CommandLineSplitter.Split(commandLine, exe);
        Assert.Equal(expectedFile, file);
        Assert.Equal(expectedArgs, args);
    }

    [Fact]
    public void MatchesWindowsByClassAndTitle()
    {
        WindowSnapshot Saved(string title, string cls) => new(title, cls, new WindowRect(0, 0, 1, 1), WindowShowState.Normal, null);
        var saved = new[] { Saved("b.txt - Notepad", "Notepad"), Saved("a.txt - Notepad", "Notepad") };
        var live = new[] { new LiveWindow(1, "a.txt - Notepad", "Notepad"), new LiveWindow(2, "Untitled - Notepad", "Notepad") };

        var pairs = WindowMatcher.Match(saved, live);

        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, p => p.Saved.Title == "a.txt - Notepad" && p.Live.Handle == 1);
        Assert.Contains(pairs, p => p.Saved.Title == "b.txt - Notepad" && p.Live.Handle == 2);
    }
}

public sealed class MonitorFillTests
{
    // User's layout: 4K landscape primary at 150%, portrait 2160x3840 secondary at 175% to the right.
    private static readonly MonitorSnapshot Primary =
        new(0, @"\\.\DISPLAY1", new WindowRect(0, 0, 3840, 2160), new WindowRect(0, 0, 3840, 2088), 144, true);

    private static readonly MonitorSnapshot Portrait =
        new(1, @"\\.\DISPLAY2", new WindowRect(3840, -800, 6000, 3040), new WindowRect(3840, -800, 6000, 2956), 168, false);

    private static readonly MonitorSnapshot[] Layout = [Primary, Portrait];

    [Fact]
    public void DetectsBorderlessWindowFillingPortraitMonitor()
    {
        // ImageGlass-style: SW_SHOWNORMAL, a few px of invisible border on each side.
        var fill = MonitorFill.Detect(new WindowRect(3833, -807, 6007, 3047), Layout);

        Assert.NotNull(fill);
        Assert.Equal(FillMonitorMode.Monitor, fill!.Mode);
        Assert.Equal(@"\\.\DISPLAY2", fill.DeviceName);
        Assert.Equal(1, fill.Index);
    }

    [Fact]
    public void DetectsWorkAreaFill()
    {
        var fill = MonitorFill.Detect(new WindowRect(0, 0, 3840, 2088), Layout);

        Assert.Equal(new FillMonitorRef(FillMonitorMode.WorkArea, @"\\.\DISPLAY1", 0), fill);
    }

    [Theory]
    [InlineData(100, 100, 1900, 1100)]
    [InlineData(3840, -800, 5000, 3040)]
    [InlineData(3800, -840, 6040, 3080)]
    public void NormalWindowsAreNotFills(int l, int t, int r, int b) =>
        Assert.Null(MonitorFill.Detect(new WindowRect(l, t, r, b), Layout));

    [Fact]
    public void RestoreUsesLiveBoundsOfSameDevice()
    {
        // Secondary moved to the left and scaling changed: bounds differ, device name is the same.
        var moved = Portrait with { Bounds = new WindowRect(-2160, -1000, 0, 2840), WorkArea = new WindowRect(-2160, -1000, 0, 2780), Dpi = 192 };
        var saved = Window(new WindowRect(3840, -800, 6000, 3040), new FillMonitorRef(FillMonitorMode.Monitor, Portrait.DeviceName, 1));

        Assert.Equal(moved.Bounds, PlacementPlanner.ResolveNormalBounds(saved, [Primary, moved]));
    }

    [Fact]
    public void RestoreFallsBackToIndexWhenDeviceRenamed()
    {
        var renamed = Portrait with { DeviceName = @"\\.\DISPLAY5" };
        var saved = Window(new WindowRect(3840, -800, 6000, 3040), new FillMonitorRef(FillMonitorMode.WorkArea, Portrait.DeviceName, 1));

        Assert.Equal(renamed.WorkArea, PlacementPlanner.ResolveNormalBounds(saved, [Primary, renamed]));
    }

    [Fact]
    public void RestoreFallsBackToClosestSizeWhenMonitorMissing()
    {
        var other = new MonitorSnapshot(0, @"\\.\DISPLAY9", new WindowRect(0, 0, 1920, 1080), new WindowRect(0, 0, 1920, 1040), 96, true);
        var saved = Window(new WindowRect(0, 0, 3840, 2160), new FillMonitorRef(FillMonitorMode.Monitor, @"\\.\DISPLAY3", 4));

        Assert.Equal(other.Bounds, PlacementPlanner.ResolveNormalBounds(saved, [other]));
    }

    [Fact]
    public void NonFillWindowKeepsSavedBounds()
    {
        var saved = Window(new WindowRect(10, 20, 810, 620), null);

        Assert.Equal(saved.NormalBounds, PlacementPlanner.ResolveNormalBounds(saved, Layout));
        Assert.Equal(saved.NormalBounds, PlacementPlanner.ResolveNormalBounds(saved with { FillsMonitor = new(FillMonitorMode.Monitor, "x", 0) }, []));
    }

    [Fact]
    public void FillAndMonitorsRoundTripThroughJson()
    {
        var app = TestData.App("ImageGlass", @"C:\ig.exe") with
        {
            Windows = [Window(Portrait.Bounds, new FillMonitorRef(FillMonitorMode.Monitor, Portrait.DeviceName, 1))]
        };
        var snapshot = TestData.Snapshot(app) with { Monitors = [.. Layout] };

        Assert.True(SnapshotSerializer.TryDeserialize(SnapshotSerializer.Serialize(snapshot), out var back));
        Assert.Equal(app.Windows[0].FillsMonitor, back!.Apps[0].Windows[0].FillsMonitor);
        Assert.Equal(Layout, back.Monitors);
    }

    [Fact]
    public void OldSnapshotsWithoutFillStillLoad()
    {
        const string json = """{"version":1,"capturedAt":"2026-10-14T10:00:00Z","machineName":"x","reason":"r","apps":[{"processName":"a","isPackaged":false,"windows":[{"title":"t","className":"c","normalBounds":{"left":1,"top":2,"right":3,"bottom":4},"showState":"normal"}]}]}""";

        Assert.True(SnapshotSerializer.TryDeserialize(json, out var back));
        Assert.Null(back!.Apps[0].Windows[0].FillsMonitor);
        Assert.Null(back.Monitors);
    }

    private static WindowSnapshot Window(WindowRect bounds, FillMonitorRef? fill) =>
        new("pic.jpg - ImageGlass", "WinUIDesktopWin32WindowClass", bounds, WindowShowState.Normal, fill?.DeviceName, fill);
}

public sealed class PackagedArgumentsTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\WindowsApps\\ImageGlass\\ImageGlass.exe\" \"D:\\Photos\\My trip\\a.jpg\"", "\"D:\\Photos\\My trip\\a.jpg\"")]
    [InlineData("ImageGlass.exe D:\\a.jpg", "D:\\a.jpg")]
    [InlineData("\"C:\\x\\App.exe\"", null)]
    [InlineData("\"C:\\x\\App.exe\" -ServerName:App.AppXabc.mca", null)]
    [InlineData("\"C:\\x\\App.exe\" -ServerName:App.mca \"E:\\b c.png\"", "\"E:\\b c.png\"")]
    [InlineData(null, null)]
    public void KeepsFileArgumentsAndDropsActivationNoise(string? commandLine, string? expected) =>
        Assert.Equal(expected, PackagedArguments.Extract(commandLine, @"C:\x\App.exe"));
}

public sealed class LaunchArgumentsTests
{
    private static readonly IReadOnlyList<string> Drop = LaunchArguments.DefaultDropArguments;
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"D:\Pics\001_city_morning.jpg",
        @"D:\Pics\119_opulent_lobby_fashion_portrait.jpg",
        @"D:\Pics\a - b.png"
    };

    private static string Rewrite(string args, params string[] titles) =>
        LaunchArguments.Rewrite(args, Drop, titles, Files.Contains, static d => d.Equals(@"D:\Pics", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("--no-startup-window /prefetch:5", "")]
    [InlineData("--profile-directory=Work --no-startup-window", "--profile-directory=Work")]
    [InlineData("--type=renderer --field-trial-handle=1", "--field-trial-handle=1")]
    [InlineData("--background", "")]
    [InlineData("--BACKGROUND --new-window", "--new-window")]
    [InlineData("--background-color=red", "--background-color=red")]
    public void DropsWindowSuppressingFlags(string args, string expected) =>
        Assert.Equal(expected, Rewrite(args));

    [Fact]
    public void ChromeDevCapturedCommandLineOpensNormally()
    {
        var (_, args) = CommandLineSplitter.Split(
            "\"C:\\Users\\g\\AppData\\Local\\Google\\Chrome Dev\\Application\\chrome.exe\" --no-startup-window /prefetch:5", null);
        Assert.Equal("", Rewrite(args));
    }

    [Fact]
    public void CustomDropListReplacesDefaults()
    {
        var result = LaunchArguments.Rewrite("--background --no-startup-window --x", ["--x"], [], _ => false, _ => false);
        Assert.Equal("--background --no-startup-window", result);
    }

    [Fact]
    public void SettingsParseDropArgumentAndDefault()
    {
        Assert.Equal(LaunchArguments.DefaultDropArguments, SessionRestoreSettings.Parse([]).DropArguments);
        var custom = SessionRestoreSettings.Parse(["DropArgument=--hidden", "DropArgument=/prefetch:*"]);
        Assert.Empty(custom.Warnings);
        Assert.Equal(["--hidden", "/prefetch:*"], custom.DropArguments);
    }

    [Fact]
    public void ViewerUsesFileShownInTitle()
    {
        var title = "119_opulent_lobby_fashion_portrait.jpg  \uFE31  119/124 file(s)  \uFE31  ImageGlass 10";
        Assert.Equal("D:\\Pics\\119_opulent_lobby_fashion_portrait.jpg", Rewrite("D:\\Pics\\001_city_morning.jpg", title));
    }

    [Theory]
    [InlineData("a - b.png - Viewer", "\"D:\\Pics\\a - b.png\"")]
    [InlineData("119_opulent_lobby_fashion_portrait.jpg \u2014 Photos", "D:\\Pics\\119_opulent_lobby_fashion_portrait.jpg")]
    [InlineData("missing.jpg - Viewer", "D:\\Pics\\001_city_morning.jpg")]
    [InlineData("ImageGlass", "D:\\Pics\\001_city_morning.jpg")]
    public void TitleFileMustExistInSameDirectory(string title, string expected) =>
        Assert.Equal(expected, Rewrite("D:\\Pics\\001_city_morning.jpg", title));

    [Fact]
    public void NoSwapWhenDirectoryMissingOrArgIsFlag()
    {
        Assert.Equal("E:\\Gone\\x.jpg", Rewrite("E:\\Gone\\x.jpg", "119_opulent_lobby_fashion_portrait.jpg - V"));
        Assert.Equal("--open=D:\\Pics\\001_city_morning.jpg", Rewrite("--open=D:\\Pics\\001_city_morning.jpg", "119_opulent_lobby_fashion_portrait.jpg - V"));
    }

    [Fact]
    public void TitleCandidatesLongestFirst()
    {
        Assert.Equal(["a.x - b.png", "a.x"], LaunchArguments.TitleFileNameCandidates("a.x - b.png - Viewer"));
        Assert.Equal(["notes.txt"], LaunchArguments.TitleFileNameCandidates("*notes.txt - Notepad"));
        Assert.Empty(LaunchArguments.TitleFileNameCandidates("Untitled - Notepad"));
    }
}

internal static class TestData
{
    public static AppSnapshot App(string name, string? exe, string? commandLine = null) =>
        new(name, exe, commandLine ?? exe, null, null, false,
            [new WindowSnapshot($"{name} window", $"{name}Class", new WindowRect(10, 20, 810, 620), WindowShowState.Maximized, @"\\.\DISPLAY1")]);

    public static SessionSnapshot Snapshot(params AppSnapshot[] apps) =>
        Snapshot(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(2)), apps);

    public static SessionSnapshot Snapshot(DateTimeOffset at, params AppSnapshot[] apps) =>
        new(SessionSnapshot.CurrentVersion, at, "BOX", "test", apps.ToList());
}
