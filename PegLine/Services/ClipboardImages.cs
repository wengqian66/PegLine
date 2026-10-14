using System.Security.Cryptography;
using PegLine.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PegLine.Services;

internal readonly record struct CapturedImage(string Path, PegOrigin Origin);

/// <summary>
/// Copies a peg out to the clipboard, and turns clipboard images into files so they can be dragged.
/// Clipboard pictures are written under %LOCALAPPDATA%\PegLine\clipboard.
/// </summary>
internal static class ClipboardImages
{
    private static string? _lastHash;

    public static async Task<CapturedImage?> TryCaptureAsync(string cacheDirectory, bool allowLooseBitmap)
    {
        DataPackageView view;
        try
        {
            view = Clipboard.GetContent();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            try
            {
                var items = await view.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is StorageFile file && ImageFiles.IsSupported(file.Path) && File.Exists(file.Path))
                    {
                        // Win+Shift+S often puts the same shot as StorageItems then as Bitmap.
                        // Fingerprint the file so the later bitmap hang is skipped.
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(file.Path);
                            var hash = Convert.ToHexString(SHA256.HashData(bytes));
                            if (hash == _lastHash)
                            {
                                return null;
                            }

                            _lastHash = hash;
                        }
                        catch
                        {
                        }

                        return new CapturedImage(file.Path, PegOrigin.Folder);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }

        if (!allowLooseBitmap || !view.Contains(StandardDataFormats.Bitmap))
        {
            return null;
        }

        try
        {
            var reference = await view.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            using var copy = new InMemoryRandomAccessStream();
            await RandomAccessStream.CopyAsync(source, copy);
            copy.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(copy);
            using var decoded = await decoder.GetSoftwareBitmapAsync();
            using var prepared = Prepare(decoded);

            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
            encoder.SetSoftwareBitmap(prepared);
            await encoder.FlushAsync();

            var bytes = await ReadAllAsync(encoded);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (hash == _lastHash)
            {
                return null;
            }

            Directory.CreateDirectory(cacheDirectory);
            var path = System.IO.Path.Combine(cacheDirectory, $"clip-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            await File.WriteAllBytesAsync(path, bytes);
            _lastHash = hash;
            return new CapturedImage(path, PegOrigin.Clipboard);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    public static void CopyFile(StorageFile file)
    {
        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.SetStorageItems(new IStorageItem[] { file });
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(package);
        try
        {
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static SoftwareBitmap Prepare(SoftwareBitmap source)
    {
        if (source.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            && source.BitmapAlphaMode is BitmapAlphaMode.Premultiplied or BitmapAlphaMode.Ignore)
        {
            return SoftwareBitmap.Copy(source);
        }

        return SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        var size = (uint)stream.Size;
        var reader = new DataReader(stream.GetInputStreamAt(0));
        try
        {
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            reader.DetachStream();
            reader.Dispose();
        }
    }
}
