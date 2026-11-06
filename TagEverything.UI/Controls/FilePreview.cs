using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.DependencyInjection;
using TagEverything.UI.Helpers;

namespace TagEverything.UI.Controls;

public sealed class FilePreview : Grid
{
    public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(
        nameof(Thumbnail), typeof(object), typeof(FilePreview), new PropertyMetadata(null, OnPreviewChanged));
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(FilePreview), new PropertyMetadata(null, OnPreviewChanged));
    public static readonly DependencyProperty RecordIdProperty = DependencyProperty.Register(
        nameof(RecordId), typeof(int), typeof(FilePreview), new PropertyMetadata(0, OnPreviewChanged));
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(string), typeof(FilePreview), new PropertyMetadata(string.Empty, OnPreviewChanged));

    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(4)
    };
    private readonly PackIcon _placeholder = new()
    {
        Kind = PackIconKind.File, Width = 48, Height = 48,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.FromRgb(79, 94, 217))
    };
    private CancellationTokenSource? _request;

    public object? Thumbnail { get => GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }
    public string? FilePath { get => (string?)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public int RecordId { get => (int)GetValue(RecordIdProperty); set => SetValue(RecordIdProperty, value); }
    public string Revision { get => (string)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

    public FilePreview()
    {
        IsHitTestVisible = false;
        Children.Add(_placeholder);
        Children.Add(_image);
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => RefreshPreview();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) RefreshPreview(); else _request?.Cancel(); };
        Unloaded += (_, _) => { _request?.Cancel(); _image.Source = null; };
    }

    private static void OnPreviewChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var preview = (FilePreview)sender;
        if (preview.IsLoaded) preview.RefreshPreview();
    }

    private async void RefreshPreview()
    {
        if (!IsVisible) return;
        _request?.Cancel();
        var request = new CancellationTokenSource();
        _request = request;
        _image.Source = null;
        _placeholder.Visibility = Visibility.Visible;
        try
        {
            if (TryShow(Thumbnail as byte[])) return;
            if (RecordId > 0)
            {
                var stored = await App.ServiceProvider.GetRequiredService<ThumbnailStoreService>()
                    .ReadAsync(App.Settings.Current.LibraryPath, RecordId, Revision, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (TryShow(stored)) return;
            }
            if (FilePath is not { Length: > 0 } path) return;
            var bytes = await App.ServiceProvider.GetRequiredService<ShellPreviewService>().GetPreviewAsync(path, request.Token);
            if (!request.IsCancellationRequested && IsLoaded) TryShow(bytes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Preview unavailable: {ex.Message}"); }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
        }
    }

    private bool TryShow(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return false;
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _image.Source = bitmap;
            _placeholder.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FileFormatException)
        {
            return false;
        }
    }
}
