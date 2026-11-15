using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Tsume.App.Mvvm;

/// <summary>Base class for anything the UI binds to.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stores a new value and tells the UI, but only if it actually changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Notify(property);
        return true;
    }

    protected void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
