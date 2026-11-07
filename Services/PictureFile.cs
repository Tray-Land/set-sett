using SetSett.Icons;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SetSett.Services;

/// <summary>Reads a picture the user chose into the icon text a setter saves (<see cref="IconPicture"/>).</summary>
internal static class PictureFile
{
    /// <summary>What the icon picker's file dialog offers.</summary>
    public static readonly IReadOnlyList<string> Types = [".ico", ".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    // Plenty to scale down from, and it keeps a phone photo from being decoded at full size.
    private const uint LargestDecode = 256;

    /// <summary>Throws when the file isn't a readable image, or nothing in it shows.</summary>
    public static async Task<string> ReadIconAsync(StorageFile file)
    {
        using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        BitmapFrame frame = await LargestFrameAsync(decoder);

        double scale = Math.Min(1, (double)LargestDecode / Math.Max(frame.OrientedPixelWidth, frame.OrientedPixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(frame.OrientedPixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(frame.OrientedPixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        PixelDataProvider data = await frame.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        // Bgra8 bytes are 0xAARRGGBB on a little-endian CPU, and Windows only runs on those.
        byte[] bytes = data.DetachPixelData();
        uint[] pixels = new uint[bytes.Length / sizeof(uint)];
        System.Buffer.BlockCopy(bytes, 0, pixels, 0, pixels.Length * sizeof(uint));

        uint[] fitted = IconPicture.Fit(pixels, (int)transform.ScaledWidth, (int)transform.ScaledHeight)
            ?? throw new InvalidDataException("The picture is fully transparent.");
        return IconPicture.Encode(fitted);
    }

    // An .ico holds the same picture at several sizes, and the biggest scales down best.
    private static async Task<BitmapFrame> LargestFrameAsync(BitmapDecoder decoder)
    {
        BitmapFrame largest = await decoder.GetFrameAsync(0);
        if (decoder.DecoderInformation.CodecId == BitmapDecoder.IcoDecoderId)
        {
            for (uint i = 1; i < decoder.FrameCount; i++)
            {
                BitmapFrame frame = await decoder.GetFrameAsync(i);
                if (frame.PixelWidth > largest.PixelWidth)
                {
                    largest = frame;
                }
            }
        }

        return largest;
    }
}
