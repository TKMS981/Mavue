using System.Runtime.InteropServices;
using Mavue.Core.Viewing;
using Mavue.Image;
using Mavue.Image.Wic;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using WinRT;

namespace Mavue.Viewer.Rendering;

/// <summary>
/// Lets WIC write decoded pixels straight into a <see cref="SoftwareBitmap"/>'s memory. Going through a
/// managed array first cost one extra full-size copy per decode (96 MB for a 24 MP photo shown at actual
/// size) and left large-object-heap garbage behind until the next full collection (measured).
/// </summary>
public static unsafe class SoftwareBitmapPixels
{
    // IMemoryBufferByteAccess (robuffer.h / MemoryBuffer.h)
    private static readonly Guid MemoryBufferByteAccessId = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    public static SoftwareBitmap FromWic(WicPreparedImage image)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, checked((int)image.Width), checked((int)image.Height), BitmapAlphaMode.Premultiplied);
        try
        {
            using BitmapBuffer buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
            using IMemoryBufferReference reference = buffer.CreateReference();
            BitmapPlaneDescription plane = buffer.GetPlaneDescription(0);
            (nint data, uint capacity) = Bytes(reference);
            image.CopyPixels((byte*)data + plane.StartIndex, checked((uint)plane.Stride), capacity - checked((uint)plane.StartIndex));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A new BGRA bitmap with <paramref name="bitmap"/> turned and mirrored as <paramref name="orientation"/> says
    /// (the input is left as is). Returns a plain copy for the identity orientation.
    /// </summary>
    public static SoftwareBitmap Orient(SoftwareBitmap bitmap, ViewOrientation orientation)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (orientation.IsIdentity)
        {
            return SoftwareBitmap.Copy(bitmap);
        }

        (int width, int height) = orientation.Oriented(bitmap.PixelWidth, bitmap.PixelHeight);
        var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, bitmap.BitmapAlphaMode);
        try
        {
            using BitmapBuffer input = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
            using IMemoryBufferReference inputReference = input.CreateReference();
            using BitmapBuffer output = result.LockBuffer(BitmapBufferAccessMode.Write);
            using IMemoryBufferReference outputReference = output.CreateReference();
            BitmapPlaneDescription inPlane = input.GetPlaneDescription(0);
            BitmapPlaneDescription outPlane = output.GetPlaneDescription(0);
            (nint inData, uint inCapacity) = Bytes(inputReference);
            (nint outData, uint outCapacity) = Bytes(outputReference);
            var source = new ReadOnlySpan<byte>((byte*)inData + inPlane.StartIndex, checked((int)(inCapacity - (uint)inPlane.StartIndex)));
            var destination = new Span<byte>((byte*)outData + outPlane.StartIndex, checked((int)(outCapacity - (uint)outPlane.StartIndex)));
            PixelOrientation.Apply(source, bitmap.PixelWidth, bitmap.PixelHeight, inPlane.Stride, destination, outPlane.Stride, orientation);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>The memory behind a locked buffer; valid while <paramref name="reference"/> is open.</summary>
    private static (nint Data, uint Capacity) Bytes(IMemoryBufferReference reference)
    {
        nint unknown = ((IWinRTObject)reference).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in MemoryBufferByteAccessId, out nint access));
        try
        {
            byte* data;
            uint capacity;
            // IMemoryBufferByteAccess::GetBuffer(BYTE** value, UINT32* capacity), first method after IUnknown.
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(getBuffer(access, &data, &capacity));
            return ((nint)data, capacity);
        }
        finally
        {
            Marshal.Release(access);
        }
    }
}
