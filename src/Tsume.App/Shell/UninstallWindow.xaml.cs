using System.Windows;
using Tsume.App.Language;
using Tsume.Core.Diagnostics;
using Tsume.Upscaling;
using Tsume.Upscaling.Install;

namespace Tsume.App.Shell;

/// <summary>
/// Asked by the uninstaller before anything is removed: a ticked checkbox offering to take OptiScaler
/// out of every game it is in and put the games' own files back.
/// </summary>
public partial class UninstallWindow
{
    private static readonly Journal Errors = new("errors");
    private readonly Installer _installer = new(new UpscalingPaths());

    public UninstallWindow()
    {
        InitializeComponent();

        Restore.Detail = Strings.RestoreGamesDetail(_installer.Removable().Count);
        Loaded += (_, _) => { Activate(); Go.Focus(); };
    }

    /// <summary>False when no game has OptiScaler in it: there is nothing to ask.</summary>
    public bool IsNeeded => _installer.Removable().Count > 0;

    private void OnToggle(object sender, RoutedEventArgs e) => Restore.IsOn = !Restore.IsOn;

    private async void OnContinue(object sender, RoutedEventArgs e)
    {
        if (!Restore.IsOn)
        {
            Close();
            return;
        }

        IsEnabled = false;
        Say(Strings.Removing);

        try
        {
            var failures = await Task.Run(() =>
            {
                var failed = new List<string>();

                foreach (var game in _installer.Removable())
                {
                    try { _installer.Remove(game.GameId); }
                    catch (Exception error) { failed.Add(Strings.CouldNotUpdate(game.GameName, Strings.Explain(error))); }
                }

                return failed;
            });

            if (failures.Count == 0)
            {
                Close();
                return;
            }

            // Stays up: Continue tries the remaining games again, or the switch can be turned off to leave them.
            Say(string.Join(Environment.NewLine, failures.Append(Strings.RestoreGamesRetry)));
        }
        catch (Exception error)
        {
            Errors.Write($"UninstallWindow restore failed: {error}");
            Say(Strings.Explain(error));
        }

        IsEnabled = true;
        Go.Focus();
    }

    private void Say(string text)
    {
        Status.Text = text;
        Status.Visibility = Visibility.Visible;
    }
}
