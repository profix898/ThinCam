using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ThinCamDemo.Infrastructure;

/// <summary>Provides property-change notification support.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Updates a field and raises <see cref="PropertyChanged" /> when its value changes.</summary>
    /// <typeparam name="T">The property value type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns><see langword="true" /> when the value changed; otherwise, <see langword="false" />.</returns>
    protected bool SetProperty<T>(ref T field,
                                  T value,
                                  [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged" /> for a property.</summary>
    /// <param name="propertyName">The property name.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
