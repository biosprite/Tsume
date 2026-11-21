using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tsume.App.ViewModels;

namespace Tsume.App.Shell;

/// <summary>
/// Development only: <c>Tsume.exe --screens &lt;folder&gt;</c> builds the panel off-screen, walks its
/// screens and saves a picture of each (or error.txt on failure). Safe beside an installed Tsume:
/// takes no focus and, except for reading the controller's lighting, touches no hardware.
/// </summary>
public static class ScreenShots
{
    /// <summary>Long enough for a screen to finish gliding in.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    public static async void Take(string folder, Action then)
    {
        Directory.CreateDirectory(folder);

        try
        {
            var panel = new PanelViewModel(SavedState.Load(), dismiss: () => Task.CompletedTask, quit: () => { });

            var window = new OverlayWindow(panel, stayOpen: true)
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = 0,
                Height = OverlayWindow.DesignHeight,   // the Claw's own screen, so drawn unscaled
            };
            window.Show();

            async Task Shot(string name)
            {
                await Task.Delay(Settle);
                Save(window, Path.Combine(folder, name + ".png"));
            }

            // --main: only the main screen, with one read-only snapshot of the helper's readings.
            if (Environment.GetCommandLineArgs().Contains("--main"))
            {
                await panel.Performance.RefreshAsync();
                await Shot("1-main");
                window.Close();
                then();
                return;
            }

            await Shot("1-main");

            panel.Settings.IsOpen = true;
            await Shot("2-settings");

            if (Descendants<Panel.SettingsScreen>(window).FirstOrDefault() is { } settingsScreen
                && Descendants<System.Windows.Controls.ScrollViewer>(settingsScreen).FirstOrDefault() is { } settingsScroll)
            {
                settingsScroll.ScrollToVerticalOffset(340);
                await Shot("2-settings-features");
                settingsScroll.ScrollToTop();
            }

            // No slider is moved, so nothing is saved or sent on closing.
            panel.Settings.PerformancePresets.IsOpen = true;
            await Shot("2c-performance-presets");
            panel.Settings.PerformancePresets.IsOpen = false;

            panel.Settings.Customization.IsOpen = true;
            await Shot("2a-customization");

            // --preset <name>: draw everything from here on in that preset, without saving it.
            var args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "--preset") is var at and >= 0 && at + 1 < args.Length
                && Theme.ColourPresets.All.FirstOrDefault(preset =>
                    preset.Name.Equals(args[at + 1], StringComparison.OrdinalIgnoreCase)) is { } chosen)
            {
                Theme.Colours.Apply(chosen.Scheme);
                await Shot("2a-customization-" + chosen.Name.ToLowerInvariant());
            }

            panel.Settings.Customization.IsOpen = false;

            panel.Settings.Controls.IsOpen = true;
            await Shot("2e-controls");
            if (Descendants<Panel.ControlsScreen>(window).FirstOrDefault() is { } controlsScreen
                && Descendants<System.Windows.Controls.ScrollViewer>(controlsScreen).FirstOrDefault() is { } controlsScroll)
            {
                controlsScroll.ScrollToVerticalOffset(420);
                await Shot("2e-controls-bottom");
                controlsScroll.ScrollToTop();
            }
            panel.Settings.Controls.IsOpen = false;

            panel.Settings.IsOpen = false;
            await Shot("1b-main-after");

            // The one hardware access: opening this screen reads the controller's lighting.
            // Nothing is changed, so nothing is written or saved on closing.
            panel.Lighting.IsOpen = true;
            await Shot("2b-lighting");
            panel.Lighting.IsOpen = false;

            panel.Rtss.IsOpen = true;
            await Shot("2d-rtss");
            panel.Rtss.PreviewForScreenShot(useTsumeOverlay: true, Platform.Rtss.RtssOverlayLevel.Bar, frameRateLimit: 45, isRunning: false);
            await Shot("2d-rtss-off");
            panel.Rtss.PreviewForScreenShot(useTsumeOverlay: true, Platform.Rtss.RtssOverlayLevel.Bar, frameRateLimit: 45, isRunning: true);
            await Shot("2d-rtss-steam");
            panel.Rtss.PreviewForScreenShot(useTsumeOverlay: false, Platform.Rtss.RtssOverlayLevel.Off, frameRateLimit: 60, isRunning: true);
            await Shot("2d-rtss-own");
            panel.Rtss.IsOpen = false;

            var upscaling = panel.Upscaling;

            upscaling.IsOpen = true;
            await Task.Delay(TimeSpan.FromSeconds(2));   // the stores are being read
            await Shot("3-optiscaler-games");

            // Only the question: the sheet is closed again without answering yes.
            upscaling.RestoreAll.Ask.Execute(null);
            await Shot("3b-optiscaler-restore-all");
            upscaling.RestoreAll.IsOpen = false;

            // One game OptiScaler can go into, and one it is not offered for.
            var ready = upscaling.List.Games.FirstOrDefault(game => game.Facts is { AntiCheat: null, Upscalers.Count: > 0 });
            var blocked = upscaling.List.Games.FirstOrDefault(game => game.Facts is { AntiCheat: not null });

            foreach (var (game, name) in new[] { (ready, "4-optiscaler-game"), (blocked, "5-optiscaler-game-not-offered") })
            {
                if (game is null) continue;

                upscaling.Show(game);
                await Shot(name);
                upscaling.Game.IsOpen = false;
            }

            upscaling.Build.IsOpen = true;
            await Shot("6-optiscaler-build");

            // Down to the last button and back up to the first: the page must end where it began, note and all.
            if (Descendants<Panel.UpscalingBuildScreen>(window).FirstOrDefault() is { } buildScreen)
            {
                var tiles = Descendants<Controls.Tile>(buildScreen).Where(tile => tile.Focusable && tile.Command != upscaling.Build.Close).ToList();
                var list = Descendants<System.Windows.Controls.ScrollViewer>(buildScreen).First();

                tiles[^1].Focus();
                tiles[^1].BringIntoView();
                await Shot("6a-optiscaler-build-at-the-bottom");
                var down = list.VerticalOffset;

                tiles[0].Focus();
                tiles[0].BringIntoView();
                await Shot("6a-optiscaler-build-back-at-the-top");

                File.WriteAllText(Path.Combine(folder, "scroll.txt"),
                    $"can scroll {list.ScrollableHeight:0}; at the last button {down:0}; back at the first {list.VerticalOffset:0}{Environment.NewLine}");
            }

            // --add-xmx: press the build page's own button, taking in the XMX upscaler
            // found on the Desktop or in Downloads exactly as by hand.
            if (Environment.GetCommandLineArgs().Contains("--add-xmx"))
            {
                upscaling.Build.AddXmx.Execute(null);
                do await Task.Delay(100); while (upscaling.IsBusy);

                File.WriteAllText(Path.Combine(folder, "xmx.txt"),
                    $"{upscaling.Build.Status}{Environment.NewLine}{upscaling.Build.XmxLabel}: {upscaling.Build.XmxLine}{Environment.NewLine}");
                await Shot("6b-optiscaler-build-xmx-added");
            }

            upscaling.Build.IsOpen = false;

            upscaling.Defaults.IsOpen = true;
            await Shot("7-optiscaler-defaults");
            upscaling.Defaults.IsOpen = false;

            upscaling.Browse.IsOpen = true;
            await Shot("8-optiscaler-add-a-game");
            upscaling.Browse.IsOpen = false;

            await WalkMadeUpGame(upscaling, Shot, folder);

            window.Close();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(folder, "error.txt"), error.ToString());
        }

        then();
    }

    /// <summary>
    /// Takes a game's page through new, installed, changed and removed by pressing its own buttons,
    /// on a made-up game in %TEMP%. What is on disk after each press goes to notes.txt.
    /// </summary>
    private static async Task WalkMadeUpGame(UpscalingViewModel upscaling, Func<string, Task> shot, string folder)
    {
        var game = Path.Combine(Path.GetTempPath(), "Tsume-screens-" + Guid.NewGuid().ToString("N")[..8], "Made Up Game");
        var program = Path.Combine(game, "Made Up Game.exe");
        var notes = new List<string>();
        string? id = null;

        Directory.CreateDirectory(game);
        File.WriteAllBytes(program, new byte[4096]);
        File.WriteAllText(Path.Combine(game, "nvngx_dlss.dll"), "stand-in for DLSS");

        async Task Press(System.Windows.Input.ICommand button)
        {
            button.Execute(null);
            do await Task.Delay(100); while (upscaling.IsBusy);
        }

        string Found() => string.Join(", ", Directory.EnumerateFileSystemEntries(game).Select(Path.GetFileName).Order());

        try
        {
            upscaling.AddByProgram(program);   // as the browser does when a program is pressed
            id = upscaling.List.Games.First(listed => listed.Game.Executable == program).Game.Id;
            await shot("9-made-up-game-added");

            await Press(upscaling.Game.Primary);
            notes.Add($"after Install:  {Found()}");
            notes.Add($"                status line: {upscaling.Game.Status}");

            var custom = Path.Combine(game, "OptiScaler", "custom");
            notes.Add($"                custom folder: {(Directory.Exists(custom) ? string.Join(", ", Directory.EnumerateFiles(custom, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(custom, file))) : "none")}");
            notes.Add($"                settings file: {string.Join("  ", File.ReadAllLines(Path.Combine(game, "OptiScaler.ini")).Where(line => line.StartsWith("Dx12Upscaler") || line.StartsWith("[xmxsr]") || line.StartsWith("[Plugin.") || line.StartsWith("Model=") || line.StartsWith("UpscalerIndex=")))}");
            await shot("10-made-up-game-installed");

            upscaling.Game.Tuning.FrameGenIndex = 1;   // XeFG
            var ini = File.ReadAllLines(Path.Combine(game, "OptiScaler.ini"));
            notes.Add($"after XeFG:     {string.Join("  ", ini.Where(line => line.StartsWith("FGOutput") || line.StartsWith("FGInput") || line.StartsWith("Dx12Upscaler")))}");
            await shot("11-made-up-game-frame-generation");

            upscaling.Game.IsOpen = false;
            await shot("12-games-with-one-installed");

            upscaling.Show(upscaling.List.Find(id)!);
            await Press(upscaling.Game.Primary);   // which now says Remove
            notes.Add($"after Remove:   {Found()}");
            notes.Add($"                status line: {upscaling.Game.Status}");
            await shot("13-made-up-game-removed");

            upscaling.Game.TakeOffList.Execute(null);
            notes.Add($"taken off the list: {upscaling.List.Find(id) is null}");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "notes.txt"), notes);

            // Leave nothing of the made-up game behind, in %TEMP% or in Tsume's own folder.
            try
            {
                Directory.Delete(Path.GetDirectoryName(game)!, recursive: true);
                if (id is not null && Directory.Exists(upscaling.Paths.InstallFolder(id)))
                    Directory.Delete(upscaling.Paths.InstallFolder(id), recursive: true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>Draws the window's contents over a plain backdrop, at twice the size for legibility.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static void Save(Window window, string file)
    {
        const double scale = 2;

        var size = new Size(window.ActualWidth, window.ActualHeight);
        var picture = new RenderTargetBitmap(
            (int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        var backdrop = new DrawingVisual();
        using (var draw = backdrop.RenderOpen())
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x3C)), null, new Rect(size));

        picture.Render(backdrop);
        picture.Render((Visual)window.Content);

        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(picture));

        using var stream = File.Create(file);
        png.Save(stream);
    }
}
