using System.Security.Cryptography;
using System.Text;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Library;

/// <summary>
/// Remembers each analysed track's waveform on disk, so loading a track the app has
/// already analysed does not decode it again just to redraw the same picture.
/// </summary>
/// <remarks>
/// One small file per track, named by a hash of its full path. Each records the audio
/// file's <see cref="FileStamp"/>, so an edited file misses and is decoded afresh. The
/// cache is only an optimisation: every failure, reading or writing, is a miss or a no-op.
/// </remarks>
public sealed class WaveformCache(string directory)
{
    private const uint Magic = 0x4D4B5746; // "MKWF"
    private const int Version = 1;
    // Magic, version, size, write ticks, peak count.
    private const int HeaderBytes = 4 + 4 + 8 + 8 + 4;
    private const int MaxPeaks = 1 << 20;

    /// <summary>Under <c>MkbMixer/waveforms</c>, beside tracks.json.</summary>
    public static WaveformCache Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MkbMixer", "waveforms"));

    /// <returns>The cached waveform, or null if there is none, the file has changed, or the cache file is damaged.</returns>
    public Waveform? TryLoad(string path, FileStamp stamp)
    {
        try
        {
            byte[] data = File.ReadAllBytes(FileFor(path));
            if (data.Length < HeaderBytes) return null;
            var span = data.AsSpan();
            if (BitConverter.ToUInt32(span[0..4]) != Magic) return null;
            if (BitConverter.ToInt32(span[4..8]) != Version) return null;
            if (BitConverter.ToInt64(span[8..16]) != stamp.Size) return null;
            if (BitConverter.ToInt64(span[16..24]) != stamp.WriteUtc.Ticks) return null;
            int count = BitConverter.ToInt32(span[24..28]);
            if (count < 0 || count > MaxPeaks || data.Length != HeaderBytes + count) return null;

            var peaks = new float[count];
            for (int i = 0; i < count; i++) peaks[i] = data[HeaderBytes + i] / 255f;
            return new Waveform(peaks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the waveform via a temp file so a crash mid-write cannot leave a half file. Failures are swallowed.</summary>
    public void Save(string path, FileStamp stamp, Waveform waveform)
    {
        try
        {
            float[] peaks = waveform.Peaks;
            var data = new byte[HeaderBytes + peaks.Length];
            var span = data.AsSpan();
            BitConverter.TryWriteBytes(span[0..4], Magic);
            BitConverter.TryWriteBytes(span[4..8], Version);
            BitConverter.TryWriteBytes(span[8..16], stamp.Size);
            BitConverter.TryWriteBytes(span[16..24], stamp.WriteUtc.Ticks);
            BitConverter.TryWriteBytes(span[24..28], peaks.Length);
            for (int i = 0; i < peaks.Length; i++)
                data[HeaderBytes + i] = (byte)Math.Round(Math.Clamp(peaks[i], 0f, 1f) * 255f);

            Directory.CreateDirectory(directory);
            string file = FileFor(path);
            // Unique per write: the deck and the background worker can save the same track at once.
            string temp = $"{file}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, file, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort */ }
                throw;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A full disk or read-only folder just means the next load decodes.
        }
    }

    /// <summary>Deletes a track's cached waveform, for a track that no longer exists.</summary>
    public void Forget(string path)
    {
        try { File.Delete(FileFor(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private string FileFor(string path) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".wf");
}
