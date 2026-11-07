using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BeaverBuddies.Util
{
    /**
     * A short hash of a mod's data files: its blueprints and every other JSON
     * under its folder. Two copies of a mod with the same DLL but a different
     * number in a blueprint play differently, so players compare this too.
     */
    public static class ContentHash
    {
        public static string Of(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return "nofiles";
            try
            {
                using var sha = SHA1.Create();
                var files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
                    .Select(f => (Path: f, Name: Path.GetRelativePath(folder, f).Replace('\\', '/')))
                    // The Workshop's own record of the upload changes with every upload, not the game
                    .Where(f => Path.GetFileName(f.Name) != "workshop_data.json")
                    .OrderBy(f => f.Name, StringComparer.Ordinal);
                foreach (var file in files)
                {
                    byte[] name = Encoding.UTF8.GetBytes(file.Name + "\n");
                    sha.TransformBlock(name, 0, name.Length, null, 0);
                    byte[] bytes = File.ReadAllBytes(file.Path);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return string.Concat(sha.Hash.Take(4).Select(b => b.ToString("x2")));
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"Couldn't hash the files in {folder}: {e.Message}");
                return "nofiles";
            }
        }
    }
}
