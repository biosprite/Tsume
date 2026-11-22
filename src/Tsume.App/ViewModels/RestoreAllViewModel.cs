using System.IO;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.Upscaling.Games;

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

            var games = _hub.Installer.Removable()
                .Select(manifest => (Manifest: manifest, Listed: _hub.List.Find(manifest.GameId)))
                .ToList();

            var (restored, failures, updatedFacts) = await Task.Run(() =>
            {
                var count = 0;
                var failed = new List<string>();
                var refreshed = new List<(ListedGame Listed, GameFacts? Facts)>();

                // One game failing (e.g. it is running) does not stop the rest.
                foreach (var (manifest, listed) in games)
                {
                    try
                    {
                        _hub.Installer.Remove(manifest.GameId);
                        count++;

                        if (listed is not null)
                        {
                            try
                            {
                                refreshed.Add((listed, GameInspector.Inspect(listed.Game)));
                            }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                            {
                                refreshed.Add((listed, listed.Facts is { } previous ? previous with { ExistingProxy = null } : null));
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        failed.Add(Strings.CouldNotUpdate(manifest.GameName, Strings.Explain(error)));
                    }
                }

                return (count, failed, refreshed);
            });

            if (updatedFacts.Count > 0)
            {
                foreach (var (listed, facts) in updatedFacts) listed.Facts = facts;
                _hub.SaveList();
            }

            Line = string.Join(Environment.NewLine, failures.Prepend(Strings.RestoredGames(restored)));
        }
        finally
        {
            _hub.IsBusy = false;
            _hub.ShowGames();
        }
    }
}
