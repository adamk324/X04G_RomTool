using System.Text;
using System.Xml.Linq;
using X04G.Core.Common;
using X04G.Core.ImageProcessing;
using X04G.Core.Preparation;

namespace X04G.Core.RomProfiles;

/// <summary>A known, verified fix the toolkit can offer for a particular vendor/system combination.</summary>
public sealed class KnownFixCandidate
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; init; }
    /// <summary>True when the selected system image is known to be affected (e.g. Android 16+).</summary>
    public bool Recommended { get; init; }
    public required string RecommendationReason { get; init; }
    /// <summary>Generated in-place file patches (content computed from the user's own vendor files).</summary>
    public List<FilePatch> Patches { get; } = new();
    /// <summary>Codec names (or other items) the fix removes.</summary>
    public List<string> AffectedItems { get; } = new();
    /// <summary>Human-readable diff of every generated patch.</summary>
    public string DiffText { get; set; } = "";
    /// <summary>Non-null if the fix is needed but cannot be applied automatically.</summary>
    public string? Blocker { get; set; }
}

/// <summary>
/// "MP2 fix": Android 16's native AudioCapabilities (libmedia_codeclist_capabilities) has no limits for
/// MPEG audio Layer I/II (audio/mpeg-L1, audio/mpeg-L2). For an OMX decoder of those types without a
/// channel-count limit, the input-channel range ends up empty and getMaxInputChannelCount() underflows
/// size()-1, which the UBSan-instrumented library turns into an abort ("ubsan: sub-overflow") inside
/// system_server while it builds the codec list (StorageManagerService.handleSystemReady). Boot never completes.
/// The Android 10 vendor OMX store does expose these types (they have OMX roles mp1/mp2), so stock MediaTek
/// vendors that declare OMX.MTK.AUDIO.DECODER.MP2 hit it. Verified on a Xiaomi Mi Smart Clock X04G:
/// removing just that declaration lets Android 16 boot, with audio working.
///
/// The fix removes only the affected &lt;MediaCodec&gt; elements, byte for byte, leaving everything else in the
/// file (comments, whitespace, other codecs) unchanged, and verifies the result by re-parsing it.
/// </summary>
public static class Mp2CodecFix
{
    public const string Id = "mp2-codec";
    public const string Partition = "vendor";
    public const int FirstAffectedSdk = 36; // Android 16 introduced the native AudioCapabilities port
    public static readonly string[] AffectedTypes = { "audio/mpeg-L1", "audio/mpeg-L2" };

    public sealed record AffectedCodec(string File, string Name, IReadOnlyList<string> Types, bool HasChannelLimit, bool MixedTypes);

    /// <summary>Inspects the vendor image and builds the fix (if any codec is affected). Never writes anything.</summary>
    public static KnownFixCandidate? Detect(Stream vendorImage, int? systemSdk)
    {
        Ext4Image fs;
        try { fs = new Ext4Image(vendorImage); } catch { return null; }
        if (!fs.CanReadFiles) return null;

        // media_codecs.xml (+ its Includes) and media_codecs_performance.xml, as the vendor OMX store reads them.
        var files = new List<string>();
        var queue = new Queue<string>(new[] { "media_codecs.xml", "media_codecs_performance.xml" });
        var contents = new Dictionary<string, byte[]>();
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (contents.ContainsKey(name)) continue;
            var data = fs.ReadFile("/etc/" + name);
            if (data == null) continue;
            contents[name] = data;
            files.Add(name);
            try
            {
                foreach (var inc in XDocument.Parse(Encoding.UTF8.GetString(data)).Descendants("Include"))
                    if (inc.Attribute("href")?.Value is string href && !href.Contains('/')) queue.Enqueue(href);
            }
            catch { /* malformed file: nothing to follow */ }
        }

        var affected = new List<AffectedCodec>();
        foreach (var name in files)
        {
            XDocument doc;
            try { doc = XDocument.Parse(Encoding.UTF8.GetString(contents[name])); } catch { continue; }
            foreach (var codec in doc.Descendants("MediaCodec"))
            {
                if (codec.Parent?.Name.LocalName != "Decoders") continue;
                var types = new List<string>();
                if (codec.Attribute("type")?.Value is string t) types.Add(t);
                types.AddRange(codec.Elements("Type").Select(e => e.Attribute("name")?.Value).OfType<string>());
                if (!types.Any(x => AffectedTypes.Contains(x, StringComparer.OrdinalIgnoreCase))) continue;
                bool limit = codec.Descendants("Limit").Any(l => l.Attribute("name")?.Value == "channel-count");
                if (limit) continue; // a channel-count limit keeps the channel range non-empty: not affected
                bool mixed = types.Any(x => !AffectedTypes.Contains(x, StringComparer.OrdinalIgnoreCase));
                affected.Add(new AffectedCodec(name, codec.Attribute("name")?.Value ?? "?", types, limit, mixed));
            }
        }
        if (affected.Count == 0) return null;

        bool recommended = systemSdk is int s && s >= FirstAffectedSdk;
        var cand = new KnownFixCandidate
        {
            Id = Id,
            Title = "Remove the MPEG Layer I/II (MP2) audio decoder declaration (Android 16 boot fix)",
            Explanation =
                "Android 16 aborts system_server while building the media codec list when the vendor declares an " +
                "audio/mpeg-L2 (or L1) OMX decoder without a channel-count limit (\"ubsan: sub-overflow\" in " +
                "AudioCapabilities::getDefaultFormat). The boot animation then never ends. Removing only this codec " +
                "declaration fixes it; MP2 is a rarely used format and all other codecs (MP3, AAC, …) stay. " +
                "Verified on a Mi Smart Clock X04G running Android 16 (TrebleDroid), audio working.",
            Recommended = recommended,
            RecommendationReason = systemSdk is int sdk
                ? (recommended ? $"The selected system is Android SDK {sdk}, which is affected." : $"The selected system is SDK {sdk}; only Android 16 (SDK {FirstAffectedSdk}) and newer are affected, so this fix is not needed.")
                : "The system's Android version is unknown; enable this fix if the system is Android 16 or newer.",
        };
        cand.AffectedItems.AddRange(affected.Select(a => $"{a.Name} ({string.Join(", ", a.Types)}) in /vendor/etc/{a.File}"));

        var mixedOnes = affected.Where(a => a.MixedTypes).ToList();
        if (mixedOnes.Count > 0)
        {
            cand.Blocker = $"{string.Join(", ", mixedOnes.Select(m => m.Name))} also declare other types; removing them would drop those too, so this is not done automatically.";
            return cand;
        }

        var diff = new StringBuilder();
        foreach (var grp in affected.GroupBy(a => a.File))
        {
            var original = contents[grp.Key];
            var patched = RemoveCodecs(original, grp.Select(a => a.Name).ToList());
            var path = "/etc/" + grp.Key;
            cand.Patches.Add(new FilePatch(Partition, path, $"(generated from your vendor: {grp.Key} without {string.Join(", ", grp.Select(a => a.Name))})")
            {
                Content = patched,
                Origin = Id,
            });
            diff.Append(Diff(path, original, patched));
        }
        cand.DiffText = diff.ToString();
        return cand;
    }

    /// <summary>
    /// Removes the named &lt;MediaCodec&gt; elements from the raw file text, keeping every other byte. Removes the
    /// element's own lines and one blank separator line after it. Verifies that exactly those elements are gone and
    /// all other MediaCodec elements are unchanged. Throws if any check fails.
    /// </summary>
    public static byte[] RemoveCodecs(byte[] original, IReadOnlyList<string> codecNames)
    {
        var text = Encoding.UTF8.GetString(original);
        bool hasBom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        if (hasBom) text = text.TrimStart('﻿');
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";

        foreach (var name in codecNames)
        {
            var marker = $"<MediaCodec name=\"{name}\"";
            int at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0 || text.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0)
                throw new ToolkitException($"Codec {name} must appear exactly once to be removed automatically.");
            int lineStart = text.LastIndexOf('\n', at) + 1;
            if (text[lineStart..at].Trim().Length != 0) throw new ToolkitException($"Codec {name} does not start on its own line.");

            int openEnd = text.IndexOf('>', at);
            int end;
            if (text[openEnd - 1] == '/') end = openEnd + 1;                      // <MediaCodec ... />
            else
            {
                int close = text.IndexOf("</MediaCodec>", openEnd, StringComparison.Ordinal);
                int nested = text.IndexOf("<MediaCodec", openEnd, StringComparison.Ordinal);
                if (close < 0 || (nested >= 0 && nested < close)) throw new ToolkitException($"Could not find the end of codec {name}.");
                end = close + "</MediaCodec>".Length;
            }
            // consume rest of the line + newline, then one blank separator line
            int eol = text.IndexOf('\n', end);
            if (eol < 0 || text[end..eol].Trim().Length != 0) throw new ToolkitException($"Codec {name} is not followed by a line break.");
            end = eol + 1;
            int next = text.IndexOf('\n', end);
            if (next >= 0 && text[end..next].Trim().Length == 0) end = next + 1;
            text = text[..lineStart] + text[end..];
        }

        // Verification: well-formed, the named codecs gone, every other codec unchanged and in order.
        static List<string> Codecs(string t) => XDocument.Parse(t).Descendants("MediaCodec")
            .Select(e => e.ToString(SaveOptions.DisableFormatting)).ToList();
        var before = Codecs(Encoding.UTF8.GetString(original).TrimStart('﻿'));
        var after = Codecs(text);
        var expected = XDocument.Parse(Encoding.UTF8.GetString(original).TrimStart('﻿')).Descendants("MediaCodec")
            .Where(e => !codecNames.Contains(e.Attribute("name")?.Value))
            .Select(e => e.ToString(SaveOptions.DisableFormatting)).ToList();
        if (after.Count != before.Count - codecNames.Count || !after.SequenceEqual(expected))
            throw new ToolkitException("Verification of the generated codec file failed; no change made.");

        var bytes = Encoding.UTF8.GetBytes(text);
        return hasBom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray() : bytes;
    }

    /// <summary>Minimal unified-style diff for a single contiguous removal (enough to show this fix).</summary>
    public static string Diff(string path, byte[] a, byte[] b)
    {
        var la = Encoding.UTF8.GetString(a).Split('\n');
        var lb = Encoding.UTF8.GetString(b).Split('\n');
        int pre = 0;
        while (pre < la.Length && pre < lb.Length && la[pre] == lb[pre]) pre++;
        int suf = 0;
        while (suf < la.Length - pre && suf < lb.Length - pre && la[^(suf + 1)] == lb[^(suf + 1)]) suf++;
        var sb = new StringBuilder();
        sb.AppendLine($"--- a{path}");
        sb.AppendLine($"+++ b{path}");
        int ctx = 3, from = Math.Max(0, pre - ctx);
        sb.AppendLine($"@@ -{from + 1},{la.Length - suf - from + Math.Min(ctx, suf)} +{from + 1},{lb.Length - suf - from + Math.Min(ctx, suf)} @@");
        for (int i = from; i < pre; i++) sb.AppendLine(" " + la[i].TrimEnd('\r'));
        for (int i = pre; i < la.Length - suf; i++) sb.AppendLine("-" + la[i].TrimEnd('\r'));
        for (int i = pre; i < lb.Length - suf; i++) sb.AppendLine("+" + lb[i].TrimEnd('\r'));
        for (int i = la.Length - suf; i < Math.Min(la.Length, la.Length - suf + ctx); i++) sb.AppendLine(" " + la[i].TrimEnd('\r'));
        return sb.ToString();
    }
}
