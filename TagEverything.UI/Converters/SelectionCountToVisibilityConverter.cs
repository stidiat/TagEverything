using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TagEverything.UI.Converters
{
    public class SelectionCountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                // parameter can specify what behavior we want.
                // if parameter is "Single", return Visible only when count <= 1
                // if parameter is "Multiple", return Visible only when count > 1
                string param = parameter as string ?? "Single";

                if (param == "Single")
                {
                    return count <= 1 ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (param == "Multiple")
                {
                    return count > 1 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}