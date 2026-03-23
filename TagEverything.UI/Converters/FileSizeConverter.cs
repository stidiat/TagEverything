using System;
using System.Globalization;
using System.Windows.Data;

namespace TagEverything.UI.Converters
{
    public class FileSizeConverter : IValueConverter
    {
        private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes)
            {
                if (bytes == 0) return "0 B";
                
                int place = System.Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
                
                // Prevent IndexOutOfRangeException if the file is absurdly large
                if (place >= SizeSuffixes.Length)
                {
                    place = SizeSuffixes.Length - 1;
                }

                double num = Math.Round(bytes / Math.Pow(1024, place), 2);
                return $"{num} {SizeSuffixes[place]}";
            }
            
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}