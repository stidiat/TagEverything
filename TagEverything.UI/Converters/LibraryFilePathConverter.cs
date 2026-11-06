using System.Globalization;
using System.Windows.Data;
using Microsoft.Extensions.DependencyInjection;
using TagEverything.Core.Services;

namespace TagEverything.UI.Converters;

public sealed class LibraryFilePathConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path) return null;
        try { return App.ServiceProvider.GetRequiredService<FileManagementService>().GetLibraryFilePath(path); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException) { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
