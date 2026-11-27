using System.Drawing.Imaging;

namespace TitleOverlay;

public sealed class TitleEntry
{
    public required string Id { get; init; }
    public required string Directory { get; init; }
    public required IReadOnlyList<string> FrameFiles { get; init; }
    public string Name { get; set; } = "";

    public override string ToString() => string.IsNullOrEmpty(Name) ? Id : $"{Name}（{Id}）";
}

public sealed class TitleLibrary
{
    /// <summary>内置中文名（来源：PNG 首帧图上的书法字）。</summary>
    private static readonly Dictionary<string, string> BuiltInNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sfx_19901"] = "初入江湖",
        ["sfx_19902"] = "小有名气",
        ["sfx_19903"] = "武林新秀",
        ["sfx_19904"] = "门派翘楚",
        ["sfx_19905"] = "登堂入室",
        ["sfx_19906"] = "宗门魁首",
        ["sfx_19907"] = "武林高手",
        ["sfx_19908"] = "人中龙凤",
        ["sfx_19909"] = "名震一方",
        ["sfx_19910"] = "英雄豪杰",
        ["sfx_19911"] = "九州大侠",
        ["sfx_19912"] = "一代宗师",
        ["sfx_19913"] = "武学泰斗",
        ["sfx_19914"] = "叱咤风云",
        ["sfx_19915"] = "威震海内",
        ["sfx_19916"] = "傲视群雄",
        ["sfx_19917"] = "武林盟主",
        ["sfx_19918"] = "傲骨天成",
        ["sfx_19919"] = "独步天下",
        ["sfx_19920"] = "震古烁今",
        ["sfx_19921"] = "一击定乾坤",
        ["sfx_19922"] = "除魔卫道",
        ["sfx_19923"] = "剑光寒十九州",
        ["sfx_19924"] = "每日特惠"
    };

    public List<TitleEntry> Titles { get; } = [];

    public string Root { get; private set; } = "";

    public void Scan(string root)
    {
        Root = root;
        Titles.Clear();

        if (!Directory.Exists(root))
        {
            return;
        }

        var custom = LoadNameOverrides(root);

        foreach (var dir in Directory.GetDirectories(root))
        {
            var id = Path.GetFileName(dir);
            var frames = Directory.GetFiles(dir, "*.png", SearchOption.TopDirectoryOnly);
            if (frames.Length == 0)
            {
                continue;
            }

            Array.Sort(frames, StringComparer.OrdinalIgnoreCase);

            var entry = new TitleEntry
            {
                Id = id,
                Directory = dir,
                FrameFiles = frames
            };

            if (custom.TryGetValue(id, out var customName) && !string.IsNullOrWhiteSpace(customName))
            {
                entry.Name = customName;
            }
            else if (BuiltInNames.TryGetValue(id, out var builtIn))
            {
                entry.Name = builtIn;
            }
            else
            {
                entry.Name = id;
            }

            Titles.Add(entry);
        }

        Titles.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
    }

    private static Dictionary<string, string> LoadNameOverrides(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, "names.txt");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var sep = line.IndexOfAny(['=', ',', '\t']);
                if (sep <= 0)
                {
                    continue;
                }

                var key = line[..sep].Trim();
                var value = line[(sep + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0)
                {
                    result[key] = value;
                }
            }
        }
        catch
        {
            // 忽略名称文件错误。
        }

        return result;
    }

    public TitleEntry? Find(string id)
    {
        return Titles.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static List<Bitmap> LoadFrames(TitleEntry entry)
    {
        var frames = new List<Bitmap>(entry.FrameFiles.Count);
        foreach (var file in entry.FrameFiles)
        {
            // 一次性读入再释放文件句柄，避免运行中锁住素材目录。
            using var stream = new MemoryStream(File.ReadAllBytes(file));
            using var src = new Bitmap(stream);
            frames.Add(new Bitmap(src));
        }
        return frames;
    }

    public static List<Bitmap> ScaleFrames(IReadOnlyList<Bitmap> frames, double scale)
    {
        var result = new List<Bitmap>(frames.Count);
        foreach (var frame in frames)
        {
            int w = Math.Max(1, (int)Math.Round(frame.Width * scale));
            int h = Math.Max(1, (int)Math.Round(frame.Height * scale));
            var target = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(target))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(frame, new Rectangle(0, 0, w, h));
            }
            result.Add(target);
        }
        return result;
    }
}
