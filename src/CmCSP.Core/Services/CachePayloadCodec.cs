using System.IO.Compression;
using System.Text.Json;

namespace CmCSP.Services;

internal static class CachePayloadCodec
{
    public static byte[] Serialize<T>(T value, JsonSerializerOptions options)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, options);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(json);
        return output.ToArray();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> payload, JsonSerializerOptions options)
    {
        if (payload.Length >= 2 && payload[0] == 0x1f && payload[1] == 0x8b)
        {
            using var input = new MemoryStream(payload.ToArray());
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return JsonSerializer.Deserialize<T>(output.ToArray(), options);
        }

        return JsonSerializer.Deserialize<T>(payload, options);
    }
}
