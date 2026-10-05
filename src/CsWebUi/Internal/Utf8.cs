using System.Runtime.InteropServices;
using System.Text;

namespace CsWebUi.Internal;

internal static unsafe class Utf8
{
    internal static byte[] Encode(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (value.Contains('\0'))
        {
            throw new ArgumentException("Embedded null characters are not supported by the WebUI C string API.", parameterName);
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        var bytes = GC.AllocateUninitializedArray<byte>(checked(byteCount + 1));
        Encoding.UTF8.GetBytes(value, bytes);
        bytes[byteCount] = 0;
        return bytes;
    }

    internal static string? Decode(byte* value)
        => value is null ? null : Marshal.PtrToStringUTF8((nint)value);

    internal static string DecodeRequired(byte* value)
        => Decode(value) ?? string.Empty;
}
