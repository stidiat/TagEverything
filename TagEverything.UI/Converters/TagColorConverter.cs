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
                
                // Use first 3 bytes for RGB
                byte r = hash[0];
                byte g = hash[1];
                byte b = hash[2];

                // Adjust to make it look nicer (not too dark, not too bright)
                r = (byte)(r % 128 + 64);
                g = (byte)(g % 128 + 64);
                b = (byte)(b % 128 + 64);

                return new SolidColorBrush(Color.FromRgb(r, g, b));
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}