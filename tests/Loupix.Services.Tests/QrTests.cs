using Loupix.Core.Imaging;
using Loupix.Services.Qr;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Loupix.Services.Tests;

public class QrTests
{
    internal static PixelBuffer Barcode(BarcodeFormat format, string text, int width, int height, int margin = 2, bool utf8WithoutEci = false)
    {
        var options = utf8WithoutEci
            ? new QrCodeEncodingOptions { CharacterSet = "UTF-8", DisableECI = true }
            : new EncodingOptions();
        options.Width = width;
        options.Height = height;
        options.Margin = margin;
        var writer = new BarcodeWriterPixelData { Format = format, Options = options };
        var data = writer.Write(text);
        return new PixelBuffer(data.Width, data.Height, data.Pixels);
    }

    private static PixelBuffer Canvas(int width, int height, byte shade = 255) => TestImages.Solid(width, height, shade, shade, shade);

    [Fact]
    public void Decodes_a_qr_code()
    {
        var canvas = Canvas(400, 300);
        canvas.Blit(Barcode(BarcodeFormat.QR_CODE, "https://loupix.example/abc?x=1", 160, 160), 120, 70);

        Assert.Equal(["https://loupix.example/abc?x=1"], QrService.Decode(canvas));
    }

    [Fact]
    public void Decodes_several_codes_in_reading_order()
    {
        var canvas = Canvas(700, 300);
        canvas.Blit(Barcode(BarcodeFormat.QR_CODE, "second", 150, 150), 420, 120);
        canvas.Blit(Barcode(BarcodeFormat.QR_CODE, "first", 150, 150), 40, 20);

        Assert.Equal(["first", "second"], QrService.Decode(canvas));
    }

    [Fact]
    public void Decodes_utf8_payload_without_eci()
    {
        // What most phone/web generators produce: UTF-8 bytes and no ECI header.
        var canvas = Canvas(300, 300);
        canvas.Blit(Barcode(BarcodeFormat.QR_CODE, "héllo 世界", 200, 200, utf8WithoutEci: true), 50, 50);
        Assert.Equal(["héllo 世界"], QrService.Decode(canvas));
    }

    [Fact]
    public void Decodes_latin1_payload()
    {
        // ZXing's writer defaults to ISO-8859-1, the QR spec's default byte encoding.
        var canvas = Canvas(300, 300);
        canvas.Blit(Barcode(BarcodeFormat.QR_CODE, "café", 200, 200), 50, 50);
        Assert.Equal(["café"], QrService.Decode(canvas));
    }

    [Fact]
    public void Decodes_inverted_qr_code()
    {
        var qr = Barcode(BarcodeFormat.QR_CODE, "dark mode", 160, 160);
        var px = qr.Bgra;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)(255 - px[i]);
            px[i + 1] = (byte)(255 - px[i + 1]);
            px[i + 2] = (byte)(255 - px[i + 2]);
        }
        var canvas = Canvas(300, 300, shade: 0);
        canvas.Blit(qr, 70, 70);

        Assert.Equal(["dark mode"], QrService.Decode(canvas));
    }

    [Fact]
    public void Decodes_code_128_next_to_a_qr_code()
    {
        var canvas = Canvas(700, 400);
        canvas.Blit(Barcode(BarcodeFormat.CODE_128, "LOUPIX-12345", 300, 80, margin: 10), 20, 20);
        canvas.Blit(Barcode(BarcodeFormat.QR_CODE, "qr", 150, 150), 480, 220);

        Assert.Equal(["LOUPIX-12345", "qr"], QrService.Decode(canvas));
    }

    [Theory]
    [InlineData(BarcodeFormat.DATA_MATRIX)]
    [InlineData(BarcodeFormat.AZTEC)]
    [InlineData(BarcodeFormat.PDF_417)]
    public void Decodes_other_2d_formats(BarcodeFormat format)
    {
        // Data Matrix and Aztec detection starts at the image centre (see QrService remarks), as in a user's crop.
        var canvas = Canvas(320, 240);
        var code = Barcode(format, "loupix 2d", format == BarcodeFormat.PDF_417 ? 240 : 120, 120, margin: 4);
        canvas.Blit(code, (320 - code.Width) / 2, (240 - code.Height) / 2);

        Assert.Equal(["loupix 2d"], QrService.Decode(canvas));
    }

    [Fact]
    public void Transparent_background_is_read_as_white()
    {
        var qr = Barcode(BarcodeFormat.QR_CODE, "transparent", 160, 160);
        var px = qr.Bgra;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i] == 255) px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0; // white → fully transparent black
        }
        Assert.Equal(["transparent"], QrService.Decode(qr));
    }

    [Fact]
    public void All_zero_alpha_is_treated_as_opaque()
    {
        var canvas = Canvas(300, 300);
        canvas.Blit(Barcode(BarcodeFormat.QR_CODE, "gdi capture", 160, 160), 70, 70);
        for (int i = 3; i < canvas.Bgra.Length; i += 4) canvas.Bgra[i] = 0; // BitBlt leaves alpha at 0
        Assert.Equal(["gdi capture"], QrService.Decode(canvas));
    }

    [Fact]
    public void Returns_empty_when_nothing_found()
    {
        Assert.Empty(QrService.Decode(Canvas(200, 100)));
        Assert.Empty(QrService.Decode(Canvas(1, 1)));
    }
}
