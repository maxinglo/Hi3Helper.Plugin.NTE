namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>
/// 表示 ResList.xml 中 &lt;Package&gt;&lt;Pak&gt;&lt;Entry&gt; 元素。
/// 代表被打包在 Pak 归档文件内的单个文件条目。
/// </summary>
internal sealed class NtePakEntry
{
    /// <summary>文件相对路径</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>在 Pak 文件中的原始起始位置</summary>
    public long Start { get; set; }

    /// <summary>在 Pak 文件中的实际数据偏移</summary>
    public long Offset { get; set; }

    /// <summary>文件条目大小（字节）</summary>
    public long Size { get; set; }

    /// <summary>文件条目的 MD5 校验和</summary>
    public string Md5 { get; set; } = string.Empty;

    /// <summary>是否需要校验（check="1"）</summary>
    public bool Check { get; set; }

    /// <summary>所属 Pak 归档的 MD5</summary>
    public string PakMd5 { get; set; } = string.Empty;

    /// <summary>所属 Pak 归档的总大小</summary>
    public long PakFilesize { get; set; }
}
