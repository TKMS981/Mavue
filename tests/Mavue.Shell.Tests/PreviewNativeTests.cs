using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using Mavue.Pdf.Tests;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Shell.Tests;

/// <summary>
/// The preview handler and thumbnail provider DLL, loaded directly (no registration, no Explorer) with pdfium.dll next
/// to it, and called the way Explorer's hosts call it: initialized with a stream, thumbnails on any thread, previews on
/// an STA thread with a message loop. Skipped when the DLL has not been built (tools/build-native.ps1). Preview
/// progress is read from the DLL's diagnostics trace (HKCU\Software\Mavue\Shell "TraceFile", set for these tests).
/// </summary>
[Trait("Category", "Shell")]
public sealed class PreviewNativeTests : IDisposable
{
    private const int SOk = 0;
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Lazy<string> NativeFolder = new(PrepareNative);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "プレビュー " + Guid.NewGuid().ToString("N"));
    private readonly string _trace;

    public PreviewNativeTests()
    {
        Directory.CreateDirectory(_folder);
        _trace = Path.Combine(_folder, "trace.jsonl");
    }

    public void Dispose()
    {
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Mavue\Shell", writable: true))
        {
            key?.DeleteValue("TraceFile", throwOnMissingValue: false);
        }

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // a stream may still be closing on the preview's background thread
        }
    }

    private static string BuiltDll => Path.Combine(IdentityPackageManifestTests.RepoRoot(), "artifacts", "native", "win-x64", ShellHandlerRegistration.DllName);

    /// <summary>The DLL and pdfium.dll side by side (as in Mavue's output folder), in a folder of their own.</summary>
    private static string PrepareNative()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "native " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.Copy(BuiltDll, Path.Combine(folder, ShellHandlerRegistration.DllName));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "pdfium.dll"), Path.Combine(folder, "pdfium.dll"));
        return folder;
    }

    private static nint Library()
    {
        Assert.SkipUnless(File.Exists(BuiltDll), "Preview DLL not built (tools/build-native.ps1 needs the MSVC Build Tools).");
        return NativeLibrary.Load(Path.Combine(NativeFolder.Value, ShellHandlerRegistration.DllName));
    }

    private static unsafe object Create(Guid clsid)
    {
        nint library = Library();
        var getClassObject = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(library, "DllGetClassObject");
        Guid iidFactory = typeof(IClassFactory).GUID;
        nint factoryPointer;
        Assert.Equal(SOk, getClassObject(&clsid, &iidFactory, &factoryPointer));
        var factory = (IClassFactory)Marshal.GetObjectForIUnknown(factoryPointer);
        Marshal.Release(factoryPointer);
        Guid iid = IidUnknown;
        try
        {
            factory.CreateInstance(null, ref iid, out object instance);
            return instance;
        }
        finally
        {
            Marshal.FinalReleaseComObject(factory); // the factory keeps the module loaded while it lives
        }
    }

    private static unsafe int CanUnloadNow()
    {
        var canUnload = (delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(Library(), "DllCanUnloadNow");
        return canUnload();
    }

    private static IStream Open(string path)
    {
        Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, 0x00000040 /* STGM_READ | STGM_SHARE_DENY_NONE */, 0, false, null, out IStream stream));
        return stream;
    }

    // ------------------------------------------------------------------ thumbnails

    private sealed record Thumbnail(int Hr, int Width, int Height, byte[] Pixels, double Ms)
    {
        public (byte B, byte G, byte R, byte A) At(int x, int y)
        {
            int i = ((y * Width) + x) * 4;
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }
    }

    private static Thumbnail GetThumbnail(string path, uint size)
    {
        object provider = Create(ShellHandlerRegistration.ThumbnailProviderClsid);
        try
        {
            ((IInitializeWithStream)provider).Initialize(Open(path), 0);
            var watch = Stopwatch.StartNew();
            int hr = ((IThumbnailProvider)provider).GetThumbnail(size, out nint bitmap, out _);
            double ms = watch.Elapsed.TotalMilliseconds;
            if (hr != SOk)
            {
                Assert.Equal(0, bitmap);
                return new Thumbnail(hr, 0, 0, [], ms);
            }

            try
            {
                Assert.NotEqual(0, GetObjectW(bitmap, Marshal.SizeOf<BITMAP>(), out BITMAP info));
                Assert.Equal(32, info.bmBitsPixel);
                Assert.NotEqual(0, info.bmBits);
                byte[] pixels = new byte[info.bmWidth * info.bmHeight * 4];
                Marshal.Copy(info.bmBits, pixels, 0, pixels.Length);
                return new Thumbnail(hr, info.bmWidth, info.bmHeight, pixels, ms);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(provider);
        }
    }

    [Fact]
    public void Thumbnail_Pdf_IsTheFirstPage_FittedToTheSize()
    {
        string pdf = Path.Combine(_folder, "文書.pdf");
        File.WriteAllBytes(pdf, TestPdf.Create(3));

        Thumbnail thumbnail = GetThumbnail(pdf, 256);

        Assert.Equal(SOk, thumbnail.Hr);
        Assert.Equal(256, thumbnail.Height); // A4 portrait: the height is the limit
        Assert.InRange(thumbnail.Width, 178, 184);
        (byte b, byte g, byte r, byte a) = thumbnail.At(4, 4);
        Assert.Equal(255, a); // an opaque page
        Assert.True(r > 200 && g > 200 && b > 200, $"page corner {r},{g},{b}");
        Assert.Contains(Enumerable.Range(0, thumbnail.Width * thumbnail.Height), i => thumbnail.Pixels[i * 4] < 100); // text
    }

    [Fact]
    public void Thumbnail_Svg_IsDrawnAtTheSize()
    {
        string svg = Path.Combine(_folder, "drawing.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 50\"><rect width=\"100\" height=\"50\" fill=\"#ff0000\"/></svg>");

        Thumbnail thumbnail = GetThumbnail(svg, 200);

        Assert.Equal(SOk, thumbnail.Hr);
        Assert.Equal((200, 100), (thumbnail.Width, thumbnail.Height)); // a vector image is enlarged to the size
        Assert.Equal((0, 0, 255, 255), ((int, int, int, int))thumbnail.At(100, 50));
    }

    [Fact]
    public async Task Thumbnail_Image_IsScaledDown_NeverUp()
    {
        string large = Path.Combine(_folder, "large.png");
        string small = Path.Combine(_folder, "small.png");
        await EncodeAsync(large, BitmapEncoder.PngEncoderId, 300, 200, (255, 0, 0));
        await EncodeAsync(small, BitmapEncoder.PngEncoderId, 50, 40, (0, 255, 0));

        Thumbnail scaled = GetThumbnail(large, 100);
        Thumbnail kept = GetThumbnail(small, 256);

        Assert.Equal((100, 67), (scaled.Width, scaled.Height));
        Assert.Equal((0, 0, 255, 255), ((int, int, int, int))scaled.At(50, 33)); // B, G, R, A
        Assert.Equal((50, 40), (kept.Width, kept.Height)); // Explorer scales small thumbnails itself
    }

    [Fact]
    public async Task Thumbnail_Jpeg_FollowsTheExifOrientation()
    {
        string jpeg = Path.Combine(_folder, "rotated.jpg");
        await EncodeAsync(jpeg, BitmapEncoder.JpegEncoderId, 80, 40, (0, 0, 255), orientation: 6);

        Thumbnail thumbnail = GetThumbnail(jpeg, 256);

        Assert.Equal(SOk, thumbnail.Hr);
        Assert.Equal((40, 80), (thumbnail.Width, thumbnail.Height)); // turned 90°
    }

    [Fact]
    public void Thumbnail_BrokenEmptyOrUnsupported_FailsQuickly()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["garbage.jpg"] = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray(),
            ["empty.pdf"] = [],
            ["broken.pdf"] = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF"),
            ["text.svg"] = Encoding.UTF8.GetBytes("this is not an SVG document"),
            ["broken.svg"] = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"10\""),
            // A PNG header that claims 100000 x 100000 pixels (40 GB decoded) and has no image data.
            ["huge.png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 1, 0x86, 0xA0, 0, 1, 0x86, 0xA0, 8, 6, 0, 0, 0, 0x2C, 0x3A, 0x4E, 0x8B],
        };

        foreach ((string name, byte[] content) in files)
        {
            string path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, content);
            Thumbnail thumbnail = GetThumbnail(path, 256);
            Assert.True(thumbnail.Hr < 0, $"{name}: expected a failure, got 0x{thumbnail.Hr:X8}");
            Assert.True(thumbnail.Ms < 3000, $"{name}: {thumbnail.Ms:0} ms");
        }
    }

    // ------------------------------------------------------------------ previews

    private void EnableTrace()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Mavue\Shell", writable: true);
        key.SetValue("TraceFile", _trace, RegistryValueKind.String);
    }

    private List<JsonElement> TraceEvents()
    {
        if (!File.Exists(_trace))
        {
            return [];
        }

        using var stream = new FileStream(_trace, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
    }

    /// <summary>
    /// Shows <paramref name="path"/> in a preview handler on an STA thread (as prevhost.exe does) until
    /// <paramref name="done"/> holds for the trace, then unloads it. Returns the trace and how long Unload took.
    /// </summary>
    private (List<JsonElement> Events, double UnloadMs) Preview(string path, Func<List<JsonElement>, bool> done, int timeoutMs = 10000)
    {
        EnableTrace();
        if (File.Exists(_trace))
        {
            File.Delete(_trace);
        }

        List<JsonElement> events = [];
        double unloadMs = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                nint parent = CreateWindowExW(0, "STATIC", string.Empty, 0x80000000u | 0x02000000u /* WS_POPUP | WS_CLIPCHILDREN, hidden */, 0, 0, 640, 480, 0, 0, 0, 0);
                Assert.NotEqual(0, parent);
                object handler = Create(ShellHandlerRegistration.PreviewHandlerClsid);
                try
                {
                    ((IInitializeWithStream)handler).Initialize(Open(path), 0);
                    var preview = (IPreviewHandler)handler;
                    var rect = new RECT { Right = 640, Bottom = 480 };
                    Assert.Equal(SOk, preview.SetWindow(parent, ref rect));
                    Assert.Equal(SOk, preview.DoPreview());
                    var watch = Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < timeoutMs)
                    {
                        while (PeekMessageW(out MSG message, 0, 0, 0, 1 /* PM_REMOVE */))
                        {
                            TranslateMessage(ref message);
                            DispatchMessageW(ref message);
                        }

                        events = TraceEvents();
                        if (done(events))
                        {
                            break;
                        }

                        Thread.Sleep(10);
                    }

                    var unload = Stopwatch.StartNew();
                    Assert.Equal(SOk, preview.Unload());
                    unloadMs = unload.Elapsed.TotalMilliseconds;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(handler);
                    DestroyWindow(parent);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("preview thread failed", failure);
        }

        return (events, unloadMs);
    }

    private static JsonElement? Find(List<JsonElement> events, string name) =>
        events.FirstOrDefault(e => e.GetProperty("event").GetString() == name) is { ValueKind: JsonValueKind.Object } found ? found : null;

    private static bool Has(List<JsonElement> events, string name) => Find(events, name) is not null;

    [Fact]
    public async Task Preview_Image_IsDecodedForThePane()
    {
        string png = Path.Combine(_folder, "photo.png");
        await EncodeAsync(png, BitmapEncoder.PngEncoderId, 1200, 900, (10, 120, 200));

        (List<JsonElement> events, _) = Preview(png, e => Has(e, "preview-loaded") || Has(e, "preview-failed"));

        JsonElement loaded = Find(events, "preview-loaded") ?? throw new Xunit.Sdk.XunitException("not loaded: " + string.Join(" ", events));
        Assert.Equal("image", loaded.GetProperty("kind").GetString());
        Assert.Equal(1200, loaded.GetProperty("sourceWidth").GetInt32());
        Assert.True(loaded.GetProperty("decodedWidth").GetInt32() <= 1200);
    }

    [Fact]
    public async Task Preview_AnimatedGif_Plays()
    {
        string gif = Path.Combine(_folder, "anim.gif");
        await WriteAnimatedGifAsync(gif, 64, 48, [(0, 0, 255), (0, 255, 0), (255, 0, 0)], 5);

        (List<JsonElement> events, _) = Preview(gif, e => Has(e, "preview-frame") || Has(e, "preview-failed"));

        Assert.Equal(3, Find(events, "preview-loaded")?.GetProperty("frames").GetInt32());
        Assert.True(Has(events, "preview-frame"), "the second frame was not shown");
    }

    [Fact]
    public void Preview_Svg_IsLoaded()
    {
        string svg = Path.Combine(_folder, "drawing.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\"><circle cx=\"20\" cy=\"15\" r=\"10\" fill=\"#00f\"/></svg>");

        (List<JsonElement> events, _) = Preview(svg, e => Has(e, "preview-loaded") || Has(e, "preview-failed"));

        Assert.Equal("svg", Find(events, "preview-loaded")?.GetProperty("kind").GetString());
    }

    [Fact]
    public void Preview_LongPdf_DrawsOnlyThePagesNearTheScreen()
    {
        string pdf = Path.Combine(_folder, "long.pdf");
        File.WriteAllBytes(pdf, TestPdf.Create(300));

        (List<JsonElement> events, double unloadMs) = Preview(pdf, e => e.Count(x => x.GetProperty("event").GetString() == "preview-page") >= 2, 15000);

        Assert.Equal(300, Find(events, "preview-loaded")?.GetProperty("pages").GetInt32());
        List<JsonElement> pages = events.Where(e => e.GetProperty("event").GetString() == "preview-page").ToList();
        Assert.NotEmpty(pages);
        Assert.All(pages, p => Assert.True(p.GetProperty("drawn").GetBoolean()));
        Assert.All(pages, p => Assert.InRange(p.GetProperty("page").GetInt32(), 0, 3)); // only the first pages are drawn
        Assert.True(unloadMs < 500, $"Unload took {unloadMs:0} ms");
    }

    [Fact]
    public void Preview_Audio_IsPlayableThroughMediaFoundation()
    {
        string wav = Path.Combine(_folder, "tone.wav");
        WriteToneWav(wav, 2);

        (List<JsonElement> events, double unloadMs) = Preview(wav, e => Has(e, "preview-loaded") || Has(e, "preview-failed"));

        JsonElement loaded = Find(events, "preview-loaded") ?? throw new Xunit.Sdk.XunitException("not loaded: " + string.Join(" ", events));
        Assert.Equal("media", loaded.GetProperty("kind").GetString());
        // The media engine shuts down off the preview thread (~200 ms there made Explorer lose a key pressed meanwhile).
        Assert.True(unloadMs < 100, $"Unload took {unloadMs:0} ms");
        Assert.True(loaded.GetProperty("audio").GetBoolean());
        Assert.False(loaded.GetProperty("video").GetBoolean());
        Assert.InRange(loaded.GetProperty("duration").GetDouble(), 1.9, 2.1);
        Assert.NotNull(Find(events, "preview-media-start")); // read through the stream (a stream from a path only knows the file name)
    }

    [Fact]
    public void EncryptedPdf_ShowsThePasswordMessage_AndHasNoThumbnail()
    {
        string pdf = Path.Combine(_folder, "保護.pdf");
        File.WriteAllBytes(pdf, TestPdf.CreateEncrypted("mavue"));

        Thumbnail thumbnail = GetThumbnail(pdf, 256);
        (List<JsonElement> events, _) = Preview(pdf, e => Has(e, "preview-failed") || Has(e, "preview-shown"));

        Assert.True(thumbnail.Hr < 0, "an encrypted PDF has no thumbnail (Explorer shows its icon)");
        Assert.Equal(102, Find(events, "preview-failed")?.GetProperty("message").GetInt32()); // IDS_PASSWORD
    }

    [Theory]
    [InlineData("garbage.jpg", 101)]
    [InlineData("empty.png", 107)]
    [InlineData("broken.pdf", 101)]
    [InlineData("noise.mp4", 101)]
    public void Preview_BrokenFile_ShowsAMessage(string name, int message)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, name.StartsWith("empty", StringComparison.Ordinal) ? [] :
            name.EndsWith(".pdf", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes("%PDF-1.4\ngarbage") :
            Enumerable.Range(0, 20000).Select(i => (byte)((i * 31) ^ (i >> 3))).ToArray());

        (List<JsonElement> events, _) = Preview(path, e => Has(e, "preview-failed") || Has(e, "preview-loaded") && !name.EndsWith(".mp4", StringComparison.Ordinal));

        JsonElement failed = Find(events, "preview-failed") ?? throw new Xunit.Sdk.XunitException("no failure message: " + string.Join(" ", events));
        Assert.Equal(message, failed.GetProperty("message").GetInt32());
    }

    [Fact]
    public async Task Preview_UnloadWhileDecoding_ReturnsAtOnce_AndTheDllCanUnload()
    {
        string png = Path.Combine(_folder, "big.png");
        await EncodeAsync(png, BitmapEncoder.PngEncoderId, 6000, 4000, (90, 90, 90), noise: true);

        (List<JsonElement> events, double unloadMs) = Preview(png, _ => true); // unload right after DoPreview

        Assert.True(Has(events, "preview-start"));
        Assert.True(unloadMs < 200, $"Unload took {unloadMs:0} ms");
        var watch = Stopwatch.StartNew();
        while (CanUnloadNow() != SOk && watch.ElapsedMilliseconds < 15000)
        {
            Thread.Sleep(50); // the background decode finishes on its own
        }

        Assert.Equal(SOk, CanUnloadNow());
    }

    [Fact]
    public void Clsids_MatchTheNativeSource()
    {
        string header = File.ReadAllText(Path.Combine(IdentityPackageManifestTests.RepoRoot(), "native", "Mavue.Shell.Preview", "src", "Module.h"));
        Assert.Contains("{" + ShellHandlerRegistration.PreviewHandlerClsid.ToString().ToUpperInvariant() + "}", header, StringComparison.Ordinal);
        Assert.Contains("{" + ShellHandlerRegistration.ThumbnailProviderClsid.ToString().ToUpperInvariant() + "}", header, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ test files

    private static async Task EncodeAsync(string path, Guid encoderId, uint width, uint height, (byte R, byte G, byte B) color, ushort orientation = 0, bool noise = false)
    {
        byte[] pixels = new byte[width * height * 4];
        var random = new Random(1);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int jitter = noise ? random.Next(-40, 40) : 0;
            pixels[i] = (byte)Math.Clamp(color.B + jitter, 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(color.G + jitter, 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(color.R + jitter, 0, 255);
            pixels[i + 3] = 255;
        }

        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        if (orientation != 0)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                ["System.Photo.Orientation"] = new BitmapTypedValue(orientation, Windows.Foundation.PropertyType.UInt16),
            });
        }

        await encoder.FlushAsync();
    }

    private static async Task WriteAnimatedGifAsync(string path, int width, int height, (byte B, byte G, byte R)[] frames, int delayCentiseconds)
    {
        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);
        for (int i = 0; i < frames.Length; i++)
        {
            if (i > 0)
            {
                await encoder.GoToNextFrameAsync();
            }

            byte[] pixels = new byte[width * height * 4];
            for (int p = 0; p < pixels.Length; p += 4)
            {
                (pixels[p], pixels[p + 1], pixels[p + 2], pixels[p + 3]) = (frames[i].B, frames[i].G, frames[i].R, 255);
            }

            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                ["/grctlext/Delay"] = new BitmapTypedValue((ushort)delayCentiseconds, Windows.Foundation.PropertyType.UInt16),
            });
        }

        await encoder.FlushAsync();
    }

    /// <summary>16-bit stereo 44.1 kHz sine tone.</summary>
    private static void WriteToneWav(string path, int seconds)
    {
        const int Rate = 44100;
        int samples = Rate * seconds;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 4));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(Rate);
        writer.Write(Rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 4);
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440 * i / Rate) * 8000);
            writer.Write(v);
            writer.Write(v);
        }
    }

    // ------------------------------------------------------------------ interop

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        void CreateInstance([MarshalAs(UnmanagedType.IUnknown)] object? outer, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        void LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }

    [ComImport]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(IStream stream, uint mode);
    }

    [ComImport]
    [Guid("e357fccd-a995-4576-b01f-234630154e96")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailProvider
    {
        [PreserveSig]
        int GetThumbnail(uint size, out nint bitmap, out int alpha);
    }

    [ComImport]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        [PreserveSig]
        int SetWindow(nint parent, ref RECT rect);

        [PreserveSig]
        int SetRect(ref RECT rect);

        [PreserveSig]
        int DoPreview();

        [PreserveSig]
        int Unload();

        [PreserveSig]
        int SetFocus();

        [PreserveSig]
        int QueryFocus(out nint focus);

        [PreserveSig]
        int TranslateAccelerator(ref MSG message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, IStream? template, out IStream stream);

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(nint handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG message);
}
