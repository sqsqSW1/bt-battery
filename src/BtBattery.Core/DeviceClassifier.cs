namespace BtBattery.Core;

/// <summary>分类器能看到的原始信号。每一层 Windows API 填自己知道的字段。</summary>
public readonly record struct ClassificationInput
{
    public ushort? Appearance { get; init; }
    public uint? ClassOfDevice { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public IReadOnlyList<string>? CompatibleIds { get; init; }
    public string? PnpClass { get; init; }
}

public readonly record struct ClassificationResult(DeviceKind Kind, string Label, string Source, int Confidence)
{
    public static ClassificationResult Unknown { get; } = new(DeviceKind.Unknown, "未知", "无类型信息", 0);

    public bool IsAccessory => DeviceKindLabels.IsAccessory(Kind);
}

/// <summary>
/// 把 BLE Appearance、经典蓝牙 Class of Device、即插即用类和名称猜测合成一个类型。
/// 手机 / 电脑的设备类置信度最高，不会被名字里的「耳机」带偏。
/// 只报成音箱、但名字明显是耳机的设备，会按耳机显示。
/// </summary>
public static class DeviceClassifier
{
    public static ClassificationResult Classify(ClassificationInput input)
    {
        var candidates = new List<ClassificationResult>(6);
        AddAppearance(candidates, input.Appearance);
        AddClassOfDevice(candidates, input.ClassOfDevice);
        AddHardware(candidates, input.PnpClass, input.CompatibleIds);
        AddCategories(candidates, input.Categories);
        AddName(candidates, input.Name);
        return Pick(candidates);
    }

    public static ClassificationResult Pick(IEnumerable<ClassificationResult> candidates)
    {
        var list = candidates.Where(item => item.Confidence > 0).ToList();
        if (list.Count == 0)
            return ClassificationResult.Unknown;

        var accessory = Best(list.Where(item => item.IsAccessory));
        var hardExclude = Best(list.Where(item => item.Kind == DeviceKind.Excluded && item.Confidence >= 90));

        // 置信度 >= 90 的排除只来自「这是手机 / 电脑」。名字猜测压不过它。
        if (accessory.Confidence >= 50 && accessory.Confidence >= hardExclude.Confidence)
            return accessory;

        if (hardExclude.Confidence >= 90)
            return hardExclude;

        return Best(list);
    }

    private static ClassificationResult Best(IEnumerable<ClassificationResult> candidates)
    {
        ClassificationResult best = default;
        var any = false;
        foreach (var candidate in candidates)
        {
            if (!any || candidate.Confidence > best.Confidence)
            {
                best = candidate;
                any = true;
            }
        }

        return any ? best : default;
    }

    private static void AddAppearance(List<ClassificationResult> candidates, ushort? appearance)
    {
        if (appearance is not ushort value || value == 0)
            return;

        var category = value >> 6;
        switch (value)
        {
            case 0x03C1:
                candidates.Add(Accessory(DeviceKind.Keyboard, "键盘", "BLE Appearance 键盘", 90));
                return;
            case 0x03C2:
                candidates.Add(Accessory(DeviceKind.Mouse, "鼠标", "BLE Appearance 鼠标", 90));
                return;
            case 0x03C9:
                candidates.Add(Accessory(DeviceKind.Mouse, "触控板", "BLE Appearance 触控板", 80));
                return;
            case 0x03C3:
                candidates.Add(Accessory(DeviceKind.Gamepad, "摇杆", "BLE Appearance 摇杆", 90));
                return;
            case 0x03C4:
                candidates.Add(Accessory(DeviceKind.Gamepad, "手柄", "BLE Appearance 手柄", 90));
                return;
            case 0x0941:
                candidates.Add(Accessory(DeviceKind.Headset, "耳塞", "BLE Appearance 耳塞", 90));
                return;
            case 0x0942:
            case 0x0944:
                candidates.Add(Accessory(DeviceKind.Headset, "耳机", "BLE Appearance 耳机", 90));
                return;
            case 0x0943:
                candidates.Add(Accessory(DeviceKind.Headset, "头戴耳机", "BLE Appearance 头戴耳机", 90));
                return;
            case 0x0940:
                candidates.Add(Accessory(DeviceKind.Headset, "耳机", "BLE Appearance 可穿戴音频", 70));
                return;
            case 0x0840:
            case 0x0841:
            case 0x0842:
            case 0x0843:
            case 0x0844:
                candidates.Add(Excluded("音箱", "BLE Appearance 音箱", 75));
                return;
        }

        // 类别 1 是电话，2 是电脑。HID（15）和可穿戴音频（37）上面已经按子类处理。
        if (category is 1 or 2)
            candidates.Add(Excluded(category == 1 ? "手机" : "电脑", "BLE Appearance 手机或电脑", 95));
    }

    private static void AddClassOfDevice(List<ClassificationResult> candidates, uint? classOfDevice)
    {
        if (classOfDevice is not uint cod || cod == 0)
            return;

        var major = (int)((cod >> 8) & 0x1F);
        var minor = (int)((cod >> 2) & 0x3F);
        var hi = (minor >> 4) & 0x3;
        var lo = minor & 0x0F;

        switch (major)
        {
            case 0x01:
                candidates.Add(Excluded("电脑", "经典蓝牙设备类：电脑", 95));
                return;
            case 0x02:
                candidates.Add(Excluded("手机", "经典蓝牙设备类：手机", 95));
                return;
            case 0x07:
                candidates.Add(Excluded("可穿戴", "经典蓝牙设备类：可穿戴", 80));
                return;
            case 0x05:
                AddPeripheral(candidates, hi, lo);
                return;
            case 0x04:
                AddAudio(candidates, minor);
                return;
            case 0x08 when lo is 0x04 or 0x05:
                candidates.Add(Accessory(DeviceKind.Gamepad, "手柄", "经典蓝牙设备类：玩具手柄", 70));
                return;
        }
    }

    private static void AddPeripheral(List<ClassificationResult> candidates, int hi, int lo)
    {
        switch (hi)
        {
            case 0b01:
                candidates.Add(Accessory(DeviceKind.Keyboard, "键盘", "经典蓝牙设备类：键盘", 80));
                return;
            case 0b10:
                candidates.Add(Accessory(DeviceKind.Mouse, "鼠标", "经典蓝牙设备类：指点设备", 80));
                return;
            case 0b11:
                candidates.Add(Accessory(DeviceKind.Combo, "键鼠", "经典蓝牙设备类：键鼠一体", 80));
                return;
        }

        switch (lo)
        {
            case 0x01:
                candidates.Add(Accessory(DeviceKind.Gamepad, "摇杆", "经典蓝牙设备类：摇杆", 80));
                break;
            case 0x02:
                candidates.Add(Accessory(DeviceKind.Gamepad, "手柄", "经典蓝牙设备类：手柄", 80));
                break;
        }
    }

    private static void AddAudio(List<ClassificationResult> candidates, int minor)
    {
        switch (minor)
        {
            case 0x01:
            case 0x02:
            case 0x06:
            case 0x12:
                candidates.Add(Accessory(DeviceKind.Headset, "耳机", "经典蓝牙设备类：耳机", 80));
                break;
            case 0x04:
            case 0x07:
                // 麦克风 / 便携音频。耳塞有时会报成这两类，所以置信度低于明确的耳机。
                candidates.Add(Accessory(DeviceKind.Headset, "耳机", "经典蓝牙设备类：便携音频", 55));
                break;
            case 0x05:
            case 0x08:
            case 0x0A:
            case 0x0F:
                candidates.Add(Excluded("音箱", "经典蓝牙设备类：音箱", 75));
                break;
        }
    }

    private static void AddHardware(
        List<ClassificationResult> candidates,
        string? pnpClass,
        IReadOnlyList<string>? compatibleIds)
    {
        var joined = string.Join("\n", compatibleIds ?? Array.Empty<string>());
        var blob = (pnpClass + "\n" + joined).ToUpperInvariant();

        if (blob.Contains("HID_DEVICE_SYSTEM_MOUSE", StringComparison.Ordinal) || pnpClassEquals(pnpClass, "Mouse"))
            candidates.Add(Accessory(DeviceKind.Mouse, "鼠标", "即插即用类：鼠标", 70));
        if (blob.Contains("HID_DEVICE_SYSTEM_KEYBOARD", StringComparison.Ordinal) || pnpClassEquals(pnpClass, "Keyboard"))
            candidates.Add(Accessory(DeviceKind.Keyboard, "键盘", "即插即用类：键盘", 70));
        if (blob.Contains("HID_DEVICE_SYSTEM_GAME", StringComparison.Ordinal))
            candidates.Add(Accessory(DeviceKind.Gamepad, "手柄", "即插即用类：游戏控制器", 70));
    }

    private static void AddCategories(List<ClassificationResult> candidates, IReadOnlyList<string>? categories)
    {
        if (categories is null)
            return;

        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category))
                continue;

            var text = category.Trim().ToLowerInvariant();
            if (text is "phone" or "computer")
            {
                candidates.Add(Excluded(text == "phone" ? "手机" : "电脑", "设备类别 " + category, 90));
                continue;
            }

            if (text.Contains("mouse", StringComparison.Ordinal) || text.Contains("pointing", StringComparison.Ordinal))
                candidates.Add(Accessory(DeviceKind.Mouse, "鼠标", "设备类别 " + category, 65));
            else if (text.Contains("keyboard", StringComparison.Ordinal))
                candidates.Add(Accessory(DeviceKind.Keyboard, "键盘", "设备类别 " + category, 65));
            else if (text.Contains("headset", StringComparison.Ordinal)
                     || text.Contains("headphone", StringComparison.Ordinal)
                     || text.Contains("earbud", StringComparison.Ordinal))
                candidates.Add(Accessory(DeviceKind.Headset, "耳机", "设备类别 " + category, 65));
            else if (text.Contains("gamepad", StringComparison.Ordinal) || text.Contains("gaming", StringComparison.Ordinal))
                candidates.Add(Accessory(DeviceKind.Gamepad, "手柄", "设备类别 " + category, 65));
        }
    }

    private static void AddName(List<ClassificationResult> candidates, string? name)
    {
        var lower = name?.Trim().ToLowerInvariant();
        if (lower is null || lower.Length == 0)
            return;

        var compact = Compact(lower);

        var mouse = Score(lower, compact, "鼠标", "mouse", "mxmaster", "mxanywhere", "mxergo", "magicmouse");
        var keyboard = Score(lower, compact, "键盘", "keyboard", "keychron", "mxkeys");
        var headset = Score(
            lower,
            compact,
            "耳机",
            "耳塞",
            "耳麦",
            "头戴",
            "headset",
            "headphone",
            "headphones",
            "earbud",
            "earbuds",
            "airpods",
            "airpod",
            "freebuds",
            "pixelbuds",
            "galaxybuds",
            "wh1000",
            "wf1000",
            "quietcomfort");
        var gamepad = Score(
            lower,
            compact,
            "手柄",
            "游戏柄",
            "gamepad",
            "controller",
            "dualsense",
            "dualshock",
            "joycon",
            "8bitdo");

        if (mouse > 0 && keyboard > 0)
        {
            candidates.Add(Accessory(DeviceKind.Combo, "键鼠", "名称像键鼠套装", 50));
        }
        else if (mouse > 0)
        {
            candidates.Add(Accessory(DeviceKind.Mouse, "鼠标", "名称像鼠标", 50));
        }
        else if (keyboard > 0)
        {
            candidates.Add(Accessory(DeviceKind.Keyboard, "键盘", "名称像键盘", 50));
        }

        if (headset > 0 && headset >= gamepad)
            candidates.Add(Accessory(DeviceKind.Headset, "耳机", "名称像耳机", 50));
        else if (gamepad > 0)
            candidates.Add(Accessory(DeviceKind.Gamepad, "手柄", "名称像手柄", 50));

        // 只有在完全不像配件时，才按名字排除手机和电脑。
        if (mouse + keyboard + headset + gamepad == 0
            && Score(lower, compact, "手机", "平板", "电脑", "iphone", "ipad", "macbook", "laptop", "android", "galaxys", "galaxya", "galaxyz", "galaxynote") > 0)
        {
            candidates.Add(Excluded("手机或电脑", "名称像手机或电脑", 45));
        }
    }

    private static int Score(string lower, string compact, params string[] words)
    {
        var score = 0;
        foreach (var word in words)
        {
            if (lower.Contains(word, StringComparison.Ordinal) || compact.Contains(word, StringComparison.Ordinal))
                score++;
        }

        return score;
    }

    private static string Compact(string lower)
    {
        var buffer = new char[lower.Length];
        var count = 0;
        foreach (var ch in lower)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or >= '\u4e00' and <= '\u9fff')
                buffer[count++] = ch;
        }

        return new string(buffer, 0, count);
    }

    private static bool pnpClassEquals(string? pnpClass, string expected) =>
        string.Equals(pnpClass?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static ClassificationResult Accessory(DeviceKind kind, string label, string source, int confidence) =>
        new(kind, label, source, confidence);

    private static ClassificationResult Excluded(string label, string source, int confidence) =>
        new(DeviceKind.Excluded, label, source, confidence);
}
