using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ThinCamTests.Avalonia;

/// <summary>Shared helpers for driving controls in the headless Avalonia session.</summary>
internal static class VisualTestHelpers
{
    /// <summary>Hosts <paramref name="content" /> in a window and runs a layout pass.</summary>
    public static Window Show(Control content, double width = 400, double height = 400)
    {
        var window = new Window { Content = content, Width = width, Height = height };
        window.Show();
        Layout(window);
        return window;
    }

    /// <summary>Runs measure and arrange so templates are applied and bindings settle.</summary>
    public static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
    }

    /// <summary>Finds the first descendant of the given type, optionally matching a name.</summary>
    public static T? Find<T>(Visual root, string? name = null)
        where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T match && (name is null || match.Name == name))
                return match;

            if (Find<T>(child, name) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>Finds every descendant of the given type, in visual tree order.</summary>
    public static List<T> FindAll<T>(Visual root)
        where T : Visual
    {
        List<T> results = [];
        Walk(root);
        return results;

        void Walk(Visual node)
        {
            foreach (var child in node.GetVisualChildren())
            {
                if (child is T match)
                    results.Add(match);

                Walk(child);
            }
        }
    }

    /// <summary>Invokes a non-public instance method, for exercising internal control behaviour.</summary>
    public static void Invoke(object target, string method, params object?[] args) =>
        target.GetType()
              .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
              .Invoke(target, args);

    /// <summary>Sets a public property that has a non-public setter.</summary>
    public static void SetReadOnlyProperty(object target, string property, object value) =>
        target.GetType()
              .GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!
              .SetValue(target, value);
}
