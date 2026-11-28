using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Godhome;

// Only managed pixel data crosses onto the file worker. Unity textures are owned by StatueCarver.
internal sealed class StatuePixels
{
    internal readonly int Width, Height;
    internal readonly float PivotX, PivotY, Ppu;
    internal readonly byte[] Rgba;

    internal StatuePixels(int width, int height, float pivotX, float pivotY, float ppu, byte[] rgba)
    {
        Width = width;
        Height = height;
        PivotX = pivotX;
        PivotY = pivotY;
        Ppu = ppu;
        Rgba = rgba;
    }
}

internal static class StatueDiskCache
{
    // Bump when carving, cropping, or the pixel layout changes.
    internal static string DirectoryFor(string root, string build, bool dark) =>
        Path.Combine(root, "Godhome", "statues", "v1", DigestName(build), dark ? "dark" : "light");

    private const int DigestSize = 32;
    private const int HeaderSize = DigestSize + 5 * 4;
    private const int MaxSide = 4096;
    private static readonly object publishLock = new();
    private static int warned;

    private static string DigestName(string value)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)), 0, 16).Replace("-", "");
    }

    private static string FileFor(string dir, string key) => Path.Combine(dir, DigestName(key) + ".rgba");

    // Corrupt or missing entries fall through to the original carving path independently.
    internal static Dictionary<string, StatuePixels> Read(string dir, IEnumerable<string> keys)
    {
        var result = new Dictionary<string, StatuePixels>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return result;
        foreach (string key in keys)
        {
            try
            {
                string path = FileFor(dir, key);
                if (!File.Exists(path)) continue;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (file.Length < HeaderSize) continue;
                using var reader = new BinaryReader(file);
                byte[] expected = reader.ReadBytes(DigestSize);
                int w = reader.ReadInt32(), h = reader.ReadInt32();
                float x = reader.ReadSingle(), y = reader.ReadSingle(), ppu = reader.ReadSingle();
                if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide || file.Length != HeaderSize + (long)w * h * 4
                    || !Finite(x) || !Finite(y) || !Finite(ppu) || ppu <= 0)
                    continue;
                using var hash = SHA256.Create();
                file.Position = DigestSize;
                if (!expected.SequenceEqual(hash.ComputeHash(file))) continue;
                file.Position = HeaderSize;
                byte[] rgba = reader.ReadBytes(w * h * 4);
                if (rgba.Length == w * h * 4) result.Add(key, new StatuePixels(w, h, x, y, ppu, rgba));
            }
            catch (Exception e) { Warn(e); }
        }
        return result;
    }

    internal static void QueueWrite(string dir, string key, StatuePixels image) =>
        _ = Task.Run(() => Write(dir, key, image));

    internal static void Write(string dir, string key, StatuePixels image)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(dir);
            string path = FileFor(dir, key);
            temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var writer = new BinaryWriter(file))
            {
                file.Position = DigestSize;
                writer.Write(image.Width);
                writer.Write(image.Height);
                writer.Write(image.PivotX);
                writer.Write(image.PivotY);
                writer.Write(image.Ppu);
                writer.Write(image.Rgba);
                writer.Flush();
                file.Position = DigestSize;
                using var hash = SHA256.Create();
                byte[] checksum = hash.ComputeHash(file);
                file.Position = 0;
                writer.Write(checksum);
            }
            lock (publishLock)
            {
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { File.Replace(temp, path, null); }
            }
            temp = null;
        }
        catch (Exception e) { Warn(e); }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); }
                catch (Exception e) { Warn(e); }
            }
        }
    }

    private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    internal static void Warn(Exception e)
    {
        if (Interlocked.Exchange(ref warned, 1) == 0)
            GodhomePlugin.Log?.LogWarning($"[Godhome] 石像磁盘缓存读写失败 / statue cache I/O: {e.Message}");
    }
}
