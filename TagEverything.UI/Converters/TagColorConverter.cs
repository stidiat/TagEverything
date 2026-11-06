using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Data;
using System.Windows.Media;

namespace TagEverything.UI.Converters
{
    public class TagColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string tag && !string.IsNullOrEmpty(tag))
            {
                // Generate a consistent color based on the tag name
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(tag));

                // A restrained palette with enough contrast for white label text.
                var palette = new[]
                {
                    Color.FromRgb(79, 94, 174),
                    Color.FromRgb(47, 113, 109),
                    Color.FromRgb(130, 83, 142),
                    Color.FromRgb(157, 91, 65),
                    Color.FromRgb(66, 108, 151),
                    Color.FromRgb(104, 113, 66)
                };
                var brush = new SolidColorBrush(palette[hash[0] % palette.Length]);
                brush.Freeze();
                return brush;
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
