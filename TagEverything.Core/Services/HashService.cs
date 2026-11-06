using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace TagEverything.Core.Services
{
    public class HashService
    {
        public async Task<string> CalculateSha256Async(string filePath, CancellationToken cancellationToken = default)
        {
            using var stream = File.OpenRead(filePath);
            return await CalculateSha256Async(stream, cancellationToken);
        }

        internal static async Task<string> CalculateSha256Async(Stream stream, CancellationToken cancellationToken)
        {
            var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
            return System.Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}
