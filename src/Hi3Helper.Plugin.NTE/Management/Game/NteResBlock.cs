namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>
/// 表示 ResList.xml 中 &lt;Res&gt; 元素内的 &lt;Block&gt; 子元素。
/// 用于大文件的分块下载。
/// </summary>
internal sealed class NteResBlock
{
    /// <summary>分块索引（从0开始）</summary>
    public int Index { get; set; }

    /// <summary>该分块在文件中的起始偏移（字节）</summary>
    public long Start { get; set; }

    /// <summary>该分块的大小（字节）</summary>
    public long Size { get; set; }

    /// <summary>该分块的 MD5 校验和</summary>
    public string Md5 { get; set; } = string.Empty;
}
