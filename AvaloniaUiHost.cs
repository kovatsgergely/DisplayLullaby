using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace DisplayLullaby;

internal sealed class DisplayLullabyAvaloniaApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = DisplayLullabyTheme.RequestedThemeVariant;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AvaloniaUiHost.NotifyStarted();
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class AvaloniaUiHost
{
    private static readonly object Sync = new();
    private static readonly ManualResetEventSlim Started = new(false);
    private static Thread? _thread;
    private static Exception? _startupException;

    public static void Invoke(Action action)
    {
        EnsureStarted();
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Exception? exception = null;
        using var completed = new ManualResetEventSlim(false);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        completed.Wait();
        if (exception is not null)
        {
            throw exception;
        }
    }

    public static void Post(Action action)
    {
        EnsureStarted();
        Dispatcher.UIThread.Post(action);
    }

    public static void Shutdown()
    {
        if (_thread is null)
        {
            return;
        }

        Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        });
    }

    public static void ApplySystemTheme()
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = DisplayLullabyTheme.RequestedThemeVariant;
        }
    }

    internal static void NotifyStarted() => Started.Set();

    private static void EnsureStarted()
    {
        lock (Sync)
        {
            if (_thread is null)
            {
                Started.Reset();
                _startupException = null;
                _thread = new Thread(RunAvalonia)
                {
                    IsBackground = true,
                    Name = "DisplayLullaby Avalonia UI"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        Started.Wait();
        if (_startupException is not null)
        {
            throw new InvalidOperationException("Avalonia could not start.", _startupException);
        }
    }

    private static void RunAvalonia()
    {
        try
        {
            AppBuilder
                .Configure<DisplayLullabyAvaloniaApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .StartWithClassicDesktopLifetime(Array.Empty<string>(), ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            _startupException = ex;
            Started.Set();
        }
    }
}
