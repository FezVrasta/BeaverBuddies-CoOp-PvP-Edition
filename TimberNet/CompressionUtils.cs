using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Text;

namespace TimberNet
{
    public static class CompressionUtils
    {
        public static byte[] Compress(string text)
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(text);

            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                {
                    gzip.Write(inputBytes, 0, inputBytes.Length);
                }
                return output.ToArray();
            }
        }

        // Kept by each thread that reads: Stream.CopyTo made an 80 KB
        // buffer for every message, every tick and every cursor move
        [ThreadStatic] private static byte[]? copyBuffer;
        [ThreadStatic] private static MemoryStream? decompressed;
        private const int KeptCapacity = 1 << 20;

        public static string Decompress(byte[] compressedData)
        {
            byte[] buffer = copyBuffer ??= new byte[4096];
            MemoryStream output = decompressed ??= new MemoryStream();
            output.SetLength(0);
            using (var input = new MemoryStream(compressedData))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                }
            }
            string text = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            // Not a whole map's worth for good
            if (output.Capacity > KeptCapacity) decompressed = null;
            return text;
        }
    }
}
