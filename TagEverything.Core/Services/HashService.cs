using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace TagEverything.Core.Services
{
    public class HashService
    {
        public async Task<string> CalculateSha256Async(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return System.Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}
