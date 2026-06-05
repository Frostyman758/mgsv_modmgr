using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MgsvModMgr.Core;
using MgsvModMgr.Gui.Lang;

namespace MgsvModMgr.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Read the persisted locale + theme out of state.txt BEFORE we
        // construct MainWindow, so the XAML resolves {DynamicResource
        // Str.*} against the right language and renders in the right
        // theme variant from frame zero. We do a throwaway State load
        // here just to peek at those two fields — MainViewModel's
        // ModManager will re-read the file as normal and own it from
        // then on.
        var (lang, theme) = PeekStartupPrefs();
        LocaleRegistry.Apply(lang);
        ApplyThemeVariant(theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Cheap pre-window load of just the language code and theme mode.
    /// Defaults to <c>("en", Dark)</c> if state.txt is missing or
    /// unparseable — the first run will land on those.
    /// </summary>
    private static (string Language, ThemeMode Theme) PeekStartupPrefs()
    {
        try
        {
            // Shared XML config — see StateIo. First-launch
            // migration handles the legacy state.txt format.
            var statePath = StateIo.DefaultPath();
            var s = new State();
            StateIo.Load(s, statePath);
            return (s.Language, s.Theme);
        }
        catch
        {
            return ("en", ThemeMode.Dark);
        }
    }

    internal static void ApplyThemeVariant(ThemeMode mode)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark  => ThemeVariant.Dark,
            // ThemeVariant.Default = follow OS. Avalonia 11.3 keeps the
            // window subscribed to OS-theme-change notifications so the
            // palette flips live when the user toggles dark/light at
            // the OS level.
            _               => ThemeVariant.Default,
        };
    }
}
