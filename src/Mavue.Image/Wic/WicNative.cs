using System.Runtime.InteropServices;

namespace Mavue.Image.Wic;

/// <summary>
/// Minimal Windows Imaging Component (wincodec.h) calls through COM vtables. Each method names the
/// interface method it calls; the slot is IUnknown (3) + the method's position in the interface (and
/// its base interfaces). Slot numbers were verified against the Windows SDK headers and by measurement.
/// The WIC factory (CLSID_WICImagingFactory2) is free-threaded; these calls work from any thread.
/// </summary>
internal static unsafe partial class WicNative
{
    public const uint GenericRead = 0x80000000;
    public const int DecodeMetadataCacheOnDemand = 0;
    public const int InterpolationFant = 3;
    public const int DitherNone = 0;
    public const int PaletteCustom = 0;
    public const int ColorContextProfile = 1;
    public const int ColorContextExifColorSpace = 2;
    public const ushort VtUi2 = 18;

    public static readonly Guid PixelFormat32bppPBGRA = new("6FDDC324-4E03-4BFE-B185-3D77768DC910");

    private static readonly Guid ClsidImagingFactory2 = new("317D06E8-5F24-433D-BDF7-79CE68D8ABC2");
    private static readonly Guid IidImagingFactory = new("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
    private const uint ClsctxInprocServer = 0x1;

    /// <summary>PROPVARIANT (24 bytes on 64-bit, 16 on 32-bit; only vt and the first value bytes are read).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public ushort UInt16Value;
    }

    public static nint CreateFactory()
    {
        Check(CoCreateInstance(ClsidImagingFactory2, 0, ClsctxInprocServer, IidImagingFactory, out nint factory));
        return factory;
    }

    // IWICImagingFactory
    public static int CreateDecoderFromFilename(nint factory, string path, out nint decoder)
    {
        nint result;
        int hr;
        fixed (char* p = path)
        {
            hr = ((delegate* unmanaged[Stdcall]<nint, char*, Guid*, uint, int, nint*, int>)Slot(factory, 3))(factory, p, null, GenericRead, DecodeMetadataCacheOnDemand, &result);
        }

        decoder = result;
        return hr;
    }

    public static nint CreateFormatConverter(nint factory) => Create(factory, 10);

    public static nint CreateBitmapScaler(nint factory) => Create(factory, 11);

    public static nint CreateBitmapFlipRotator(nint factory) => Create(factory, 13);

    public static nint CreateColorContext(nint factory) => Create(factory, 15);

    // IWICBitmapDecoder
    public static nint GetFrame(nint decoder, uint index)
    {
        nint frame;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(decoder, 13))(decoder, index, &frame));
        return frame;
    }

    // IWICBitmapSource (and every interface derived from it)
    public static (uint Width, uint Height) GetSize(nint source)
    {
        uint w, h;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(source, 3))(source, &w, &h));
        return (w, h);
    }

    public static void CopyPixels(nint source, uint stride, byte[] buffer)
    {
        fixed (byte* b = buffer)
        {
            CopyPixels(source, stride, (uint)buffer.Length, b);
        }
    }

    public static void CopyPixels(nint source, uint stride, uint bufferSize, byte* buffer) =>
        Check(((delegate* unmanaged[Stdcall]<nint, void*, uint, uint, byte*, int>)Slot(source, 7))(source, null, stride, bufferSize, buffer));

    // IWICBitmapFrameDecode : IWICBitmapSource
    public static int GetMetadataQueryReader(nint frame, out nint reader)
    {
        nint result;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(frame, 8))(frame, &result);
        reader = result;
        return hr;
    }

    public static int GetColorContexts(nint frame, uint count, nint* contexts, out uint actual)
    {
        uint result;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(frame, 9))(frame, count, contexts, &result);
        actual = result;
        return hr;
    }

    // IWICBitmapScaler : IWICBitmapSource
    public static void InitializeScaler(nint scaler, nint source, uint width, uint height) =>
        Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, int, int>)Slot(scaler, 8))(scaler, source, width, height, InterpolationFant));

    // IWICBitmapFlipRotator : IWICBitmapSource
    public static void InitializeFlipRotator(nint flipRotator, nint source, int options) =>
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Slot(flipRotator, 8))(flipRotator, source, options));

    // IWICFormatConverter : IWICBitmapSource
    public static void InitializeConverter(nint converter, nint source, Guid format)
    {
        Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint, double, int, int>)Slot(converter, 8))(converter, source, &format, DitherNone, 0, 0, PaletteCustom));
    }

    // IWICMetadataQueryReader
    public static int GetMetadataByName(nint reader, string name, PropVariant* value)
    {
        fixed (char* n = name)
        {
            return ((delegate* unmanaged[Stdcall]<nint, char*, PropVariant*, int>)Slot(reader, 5))(reader, n, value);
        }
    }

    // IWICColorContext
    public static int GetColorContextType(nint context)
    {
        int type;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(context, 6))(context, &type));
        return type;
    }

    public static uint GetExifColorSpace(nint context)
    {
        uint value;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(context, 8))(context, &value));
        return value;
    }

    public static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            Marshal.Release(pointer);
        }
    }

    public static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(PropVariant* value);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);

    private static nint Create(nint factory, int slot)
    {
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, slot))(factory, &result));
        return result;
    }

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];
}
