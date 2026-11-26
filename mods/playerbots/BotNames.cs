namespace GameServer.Modules.PlayerBots;

/// <summary>Random Chinese character names, in the server's name rules (3 to 23 characters of letters, digits, '_' or '-';
/// a CJK character counts as a letter, so three characters is the shortest that passes — hence no single-character given
/// names on their own).</summary>
static class BotNames
{
    static readonly string[] Surnames =
    [
        "李", "王", "张", "刘", "陈", "杨", "赵", "周", "吴", "徐", "孙", "马", "朱", "胡", "郭", "何",
        "高", "林", "罗", "郑", "梁", "谢", "宋", "唐", "许", "韩", "冯", "邓", "曹", "彭", "曾", "萧",
        "田", "董", "袁", "潘", "蒋", "蔡", "余", "杜", "叶", "程", "苏", "魏", "吕", "丁", "任", "沈",
    ];
    static readonly string[] DoubleSurnames =
    [
        "上官", "欧阳", "司徒", "南宫", "东方", "西门", "独孤", "慕容", "令狐", "公孙",
        "皇甫", "长孙", "宇文", "轩辕", "端木", "百里", "司马", "夏侯", "尉迟", "澹台",
    ];
    static readonly string[] Given =
    [
        "云", "飞", "天", "玄", "无", "孤", "清", "风", "雪", "霜", "寒", "月", "星", "影", "尘", "墨",
        "白", "青", "苍", "夜", "秋", "春", "山", "河", "海", "川", "雷", "火", "金", "木", "水", "阳",
        "剑", "刀", "琴", "棋", "书", "酒", "茶", "松", "竹", "梅", "兰", "鹤", "鹰", "狼", "龙", "虎",
        "燕", "蝶", "舟", "岸", "沙", "烟", "雨", "霜", "砚", "弦", "锋", "戈", "爻", "岚", "曦", "照",
        "衍", "澈", "澜", "澈", "祁", "骁", "霁", "寒", "渺", "遥", "序", "珏", "堇", "瑜", "瑾", "玦",
    ];

    /// <summary>A name that <paramref name="taken"/> does not know yet: a surname with a two-character given name, a
    /// two-character surname with one, or both two-character. Digits are only appended once the pools are exhausted, so a
    /// name that reads as a name is the rule rather than the exception.</summary>
    public static string Pick(Random random, Func<string, bool> taken)
    {
        for (int attempt = 0; ; attempt++)
        {
            string name = random.Next(10) switch
            {
                < 4 => Surnames[random.Next(Surnames.Length)] + Given[random.Next(Given.Length)] + Given[random.Next(Given.Length)],
                < 7 => DoubleSurnames[random.Next(DoubleSurnames.Length)] + Given[random.Next(Given.Length)],
                _ => DoubleSurnames[random.Next(DoubleSurnames.Length)] + Given[random.Next(Given.Length)] + Given[random.Next(Given.Length)],
            };
            if (attempt > 20) name += random.Next(10, 99);
            if (name.Length is >= 3 and <= 23 && !taken(name)) return name;
        }
    }

    /// <summary>Whether a stored bot name already reads as Chinese: the migration that replaced the Russian and English
    /// pools uses it to tell which bots to replace.</summary>
    public static bool IsChinese(string name) => name.Any(ch => ch is >= '\u4e00' and <= '\u9fff');
}
