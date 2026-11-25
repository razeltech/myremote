using System.Windows;
using AppThemeMode = MyRemote.Core.Models.ThemeMode;

namespace MyRemote.Desktop.Services;

public sealed class ThemeService
{
    public AppThemeMode CurrentTheme { get; private set; } = AppThemeMode.Dark;

    public void ApplyTheme(AppThemeMode mode)
    {
        CurrentTheme = mode;
        var uri = mode == AppThemeMode.Dark
            ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
            : new Uri("Themes/LightTheme.xaml", UriKind.Relative);

        var newDict = new ResourceDictionary { Source = uri };
        var merged = Application.Current.Resources.MergedDictionaries;

        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString;
            if (src is not null &&
                (src.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                 src.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                merged.RemoveAt(i);
            }
        }

        merged.Insert(0, newDict);
    }
}
