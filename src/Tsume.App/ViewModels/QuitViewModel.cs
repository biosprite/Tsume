using System.ComponentModel;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.Core.Devices;

namespace Tsume.App.ViewModels;

/// <summary>The "Quit Tsume?" sheet: quitting takes a second, deliberate press.</summary>
public sealed class QuitViewModel : LayerViewModel
{
    private readonly PerformanceViewModel _performance;

    /// <param name="performance">Supplies the active device profile so button wording matches the hardware.</param>
    /// <param name="quit">Exits Tsume.</param>
    public QuitViewModel(PerformanceViewModel performance, Action quit)
    {
        _performance = performance;
        Confirm = new Command(quit);
        _performance.PropertyChanged += OnPerformancePropertyChanged;
    }

    public ICommand Confirm { get; }

    public string Explanation => ExplanationFor(_performance.Device);

    public static string ExplanationFor(DeviceProfile device) =>
        SettingsViewModel.IsAcer(device) ? Strings.QuitExplanationOem : Strings.QuitExplanation;

    protected override void OnOpened() => Notify(nameof(Explanation));

    private void OnPerformancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PerformanceViewModel.Device))
            Notify(nameof(Explanation));
    }
}
