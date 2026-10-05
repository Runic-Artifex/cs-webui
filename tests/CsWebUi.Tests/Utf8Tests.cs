using System.Runtime.InteropServices;
using System.Text;
using CsWebUi.Internal;

namespace CsWebUi.Tests;

public sealed class Utf8Tests
{
    [Theory]
    [InlineData("")]
    [InlineData("response")]
    [InlineData("Übersetzung 🧙 漢字")]
    public unsafe void EncodesNullTerminatedUtf8(string value)
    {
        var expected = Encoding.UTF8.GetBytes(value);
        var bytes = Utf8.Encode(value, "value");

        Assert.Equal(expected.Length + 1, bytes.Length);
        Assert.Equal(expected, bytes[..^1]);
        Assert.Equal((byte)0, bytes[^1]);
        fixed (byte* pointer = bytes)
        {
            Assert.Equal(value, Marshal.PtrToStringUTF8((nint)pointer));
        }
    }

    [Fact]
    public unsafe void LargeUnicodeResponsesRemainTerminatedAfterHeapReuse()
    {
        var value = string.Concat(Enumerable.Repeat("Übersetzung 🧙 漢字 ", 512));
        var expected = Encoding.UTF8.GetBytes(value);

        for (var iteration = 0; iteration < 128; iteration++)
        {
            // Large uninitialized arrays can reuse bytes from previous responses.
            GC.AllocateUninitializedArray<byte>(expected.Length + 1).AsSpan().Fill(0x7B);
            if (iteration % 16 == 0)
            {
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            }

            var bytes = Utf8.Encode(value, "value");
            Assert.Equal(expected.Length + 1, bytes.Length);
            Assert.Equal(expected, bytes[..^1]);
            Assert.Equal((byte)0, bytes[^1]);
            fixed (byte* pointer = bytes)
            {
                Assert.Equal(value, Marshal.PtrToStringUTF8((nint)pointer));
            }
        }
    }

    [Fact]
    public void RejectsEmbeddedNullCharacters()
    {
        Assert.Throws<ArgumentException>(() => Utf8.Encode("before\0after", "value"));
    }
}
