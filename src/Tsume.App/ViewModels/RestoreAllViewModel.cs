using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;

namespace Tsume.App.ViewModels;

/// <summary>The "Restore all games?" sheet: taking OptiScaler out of every game takes a second, deliberate press.</summary>
public sealed class RestoreAllViewModel : LayerViewModel
{
    private readonly UpscalingViewModel _hub;

    public RestoreAllViewModel(UpscalingViewModel hub)
    {
        _hub = hub;

        // Nothing to ask while there is nothing to restore, or while files are already being moved.
        Ask = new Command(() => { if (!_hub.IsBusy && _hub.Installer.Removable().Count > 0) IsOpen = true; });

        Confirm = new Command(() =>
        {
            IsOpen = false;
            _ = RestoreAsync();
        });
    }

    public ICommand Ask { get; }
    public ICommand Confirm { get; }

    /// <summary>How many games it is about, said in the sheet.</summary>
    public string Explanation { get; private set => Set(ref field, value); } = "";

    /// <summary>The line under the row on the games screen: what it does, then how it went.</summary>
    public string Line { get; private set => Set(ref field, value); } = Strings.RestoreAllDetail;

    /// <summary>Back to saying what the row does.</summary>
    public void Reset()
    {
        if (!_hub.IsBusy) Line = Strings.RestoreAllDetail;
    }

    protected override void OnOpened() => Explanation = Strings.RestoreGamesDetail(_hub.Installer.Removable().Count);

    private async Task RestoreAsync()
    {
        // A second press while the first is still at work does nothing.
        if (_hub.IsBusy) return;
        _hub.IsBusy = true;

        try
        {
            Line = Strings.Removing;

            var restored = 0;
            var failures = await Task.Run(() =>
            {
                var failed = new List<string>();

                // One game failing (e.g. it is running) does not stop the rest.
                foreach (var game in _hub.Installer.Removable())
                {
                    try
                    {
                        _hub.Installer.Remove(game.GameId);
                        restored++;
                    }
                    catch (Exception error)
                    {
                        failed.Add(Strings.CouldNotUpdate(game.GameName, Strings.Explain(error)));
                    }
                }

                return failed;
            });

            Line = string.Join(Environment.NewLine, failures.Prepend(Strings.RestoredGames(restored)));
        }
        finally
        {
            _hub.IsBusy = false;
            _hub.ShowGames();
        }
    }
}
