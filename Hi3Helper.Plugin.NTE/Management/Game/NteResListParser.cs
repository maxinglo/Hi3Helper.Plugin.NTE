using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>
/// 解析解密后的 ResList.xml，提取资源清单和 Pak 归档信息。
/// </summary>
internal sealed class NteResListParser
{
    /// <summary>直接下载的资源文件列表（&lt;Res&gt; 元素）</summary>
    public List<NteResListEntry> Resources { get; } = [];

    /// <summary>Pak 归档信息列表（&lt;Package&gt;&lt;Pak&gt; 元素）</summary>
    public List<NtePakInfo> Paks { get; } = [];

    /// <summary>BaseVersion 对应的额外资源列表</summary>
    public List<NteResListEntry> BaseVersionResources { get; } = [];

    /// <summary>BaseVersion 对应的额外 Pak 归档列表</summary>
    public List<NtePakInfo> BaseVersionPaks { get; } = [];

    /// <summary>ResList 的版本号</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>
    /// 从 XML 字节数组解析资源清单。
    /// </summary>
    public static NteResListParser Parse(byte[] xmlBytes)
    {
        string xml = System.Text.Encoding.UTF8.GetString(xmlBytes);
        return Parse(xml);
    }

    /// <summary>
    /// 从 XML 字符串解析资源清单。
    /// </summary>
    public static NteResListParser Parse(string xml)
    {
        NteResListParser parser = new();

        XmlDocument doc = new();
        doc.LoadXml(xml);

        XmlElement? root = doc.DocumentElement;
        if (root == null)
            return parser;

        parser.Version = root.GetAttribute("version");

        // 解析根级别的 <Res> 元素
        foreach (XmlNode node in root.ChildNodes)
        {
            if (node is not XmlElement element)
                continue;

            switch (element.Name)
            {
                case "Res":
                    parser.Resources.Add(ParseResElement(element));
                    break;

                case "Package":
                    ParsePackageElement(element, parser.Paks);
                    break;

                case "BaseVersion":
                    ParseBaseVersionElement(element, parser.BaseVersionResources, parser.BaseVersionPaks);
                    break;
            }
        }

        return parser;
    }

    /// <summary>
    /// 获取安装所需的全部资源总大小（字节）。
    /// 包括直接资源文件和 Pak 归档。
    /// </summary>
    public long GetTotalInstallSize()
    {
        long total = 0;

        foreach (NteResListEntry res in Resources)
            total += res.Filesize;

        foreach (NtePakInfo pak in Paks)
            total += pak.Filesize;

        foreach (NteResListEntry res in BaseVersionResources)
            total += res.Filesize;

        foreach (NtePakInfo pak in BaseVersionPaks)
            total += pak.Filesize;

        return total;
    }

    /// <summary>
    /// 获取安装后游戏实际占用的磁盘大小（字节）。
    /// 直接资源按 Filesize 计算，Pak 归档按提取后的 Entry 大小总和计算。
    /// </summary>
    public long GetTotalExtractedSize()
    {
        long total = 0;

        foreach (NteResListEntry res in Resources)
            total += res.Filesize;

        foreach (NtePakInfo pak in Paks)
        {
            foreach (NtePakEntry entry in pak.Entries)
                total += entry.Size;
        }

        foreach (NteResListEntry res in BaseVersionResources)
            total += res.Filesize;

        foreach (NtePakInfo pak in BaseVersionPaks)
        {
            foreach (NtePakEntry entry in pak.Entries)
                total += entry.Size;
        }

        return total;
    }

    /// <summary>
    /// 获取全部需要下载的文件数量。
    /// </summary>
    public int GetTotalFileCount()
    {
        int count = Resources.Count;

        foreach (NtePakInfo pak in Paks)
            count += pak.Entries.Count;

        count += BaseVersionResources.Count;

        foreach (NtePakInfo pak in BaseVersionPaks)
            count += pak.Entries.Count;

        return count;
    }

    private static NteResListEntry ParseResElement(XmlElement element)
    {
        NteResListEntry entry = new()
        {
            Filename = element.GetAttribute("filename"),
            Filesize = ParseLong(element.GetAttribute("filesize")),
            Md5 = element.GetAttribute("md5")
        };

        // 解析 <Block> 子元素
        foreach (XmlNode child in element.ChildNodes)
        {
            if (child is XmlElement { Name: "Block" } blockElement)
            {
                entry.Blocks.Add(new NteResBlock
                {
                    Index = ParseInt(blockElement.GetAttribute("index")),
                    Start = ParseLong(blockElement.GetAttribute("start")),
                    Size = ParseLong(blockElement.GetAttribute("size")),
                    Md5 = blockElement.GetAttribute("md5")
                });
            }
        }

        return entry;
    }

    private static void ParsePackageElement(XmlElement packageElement, List<NtePakInfo> pakList)
    {
        foreach (XmlNode child in packageElement.ChildNodes)
        {
            if (child is not XmlElement { Name: "Pak" } pakElement)
                continue;

            NtePakInfo pakInfo = new()
            {
                Md5 = pakElement.GetAttribute("md5"),
                Filesize = ParseLong(pakElement.GetAttribute("filesize"))
            };

            foreach (XmlNode entryNode in pakElement.ChildNodes)
            {
                if (entryNode is not XmlElement { Name: "Entry" } entryElement)
                    continue;

                NtePakEntry entry = new()
                {
                    Name = entryElement.GetAttribute("name"),
                    Start = ParseLong(entryElement.GetAttribute("start")),
                    Offset = ParseLong(entryElement.GetAttribute("offset")),
                    Size = ParseLong(entryElement.GetAttribute("size")),
                    Md5 = entryElement.GetAttribute("md5"),
                    Check = entryElement.GetAttribute("check") == "1",
                    PakMd5 = pakInfo.Md5,
                    PakFilesize = pakInfo.Filesize
                };

                pakInfo.Entries.Add(entry);
            }

            pakList.Add(pakInfo);
        }
    }

    private static void ParseBaseVersionElement(XmlElement baseVersionElement,
        List<NteResListEntry> resList, List<NtePakInfo> pakList)
    {
        // BaseVersion 下面有 <ResList> 子元素，里面包含 <Res> 和 <Package>
        foreach (XmlNode child in baseVersionElement.ChildNodes)
        {
            if (child is not XmlElement { Name: "ResList" } resListElement)
                continue;

            foreach (XmlNode resChild in resListElement.ChildNodes)
            {
                if (resChild is not XmlElement element)
                    continue;

                switch (element.Name)
                {
                    case "Res":
                        resList.Add(ParseResElement(element));
                        break;

                    case "Package":
                        ParsePackageElement(element, pakList);
                        break;
                }
            }
        }
    }

    private static long ParseLong(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : 0;
    }

    private static int ParseInt(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : 0;
    }
}
