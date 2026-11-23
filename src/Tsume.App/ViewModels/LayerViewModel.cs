using System.Windows.Input;
using Tsume.App.Mvvm;

namespace Tsume.App.ViewModels;

/// <summary>
/// Anything that covers the panel — a sheet rising from the bottom, or a whole
/// second screen. It can be opened and closed.
/// </summary>
public abstract class LayerViewModel : Observable
{
    protected LayerViewModel()
    {
        Open = new Command(() => IsOpen = true);
        Close = new Command(() => IsOpen = false);
    }

    /// <summary>True while this layer is covering what is beneath it.</summary>
    public bool IsOpen
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            if (value) OnOpened();
            else OnClosed();
        }
    }

    public ICommand Open { get; }
    public ICommand Close { get; }

    /// <summary>Called each time the layer opens, to bring what it shows up to date.</summary>
    protected virtual void OnOpened() { }

    /// <summary>Called each time the layer closes, to settle anything left pending.</summary>
    protected virtual void OnClosed() { }

    /// <summary>
    /// Called when Back (controller B / Escape) is pressed while this layer is on top.
    /// Return true to consume it internally instead of closing the layer.
    /// </summary>
    public virtual bool GoBack() => false;
}
