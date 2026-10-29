using System.Collections.Generic;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>
/// 表示 ResList.xml 中的 &lt;Res&gt; 元素。
/// 代表一个可直接下载的游戏资源文件。
/// </summary>
internal sealed class NteResListEntry
{
    /// <summary>文件相对路径，如 Client/WindowsNoEditor/HT/Content/Paks/pakchunk0-Windows.pak</summary>
    public string Filename { get; set; } = string.Empty;

    /// <summary>文件总大小（字节）</summary>
    public long Filesize { get; set; }

    /// <summary>文件整体的 MD5 校验和</summary>
    public string Md5 { get; set; } = string.Empty;

    /// <summary>
    /// 分块列表。大文件会被拆分成多个 512MB 块以支持分段下载。
    /// 如果为空则表示整个文件作为单个块下载。
    /// </summary>
    public List<NteResBlock> Blocks { get; set; } = [];

    /// <summary>是否有分块信息</summary>
    public bool HasBlocks => Blocks.Count > 0;

    /// <summary>
    /// 构建该资源的 CDN 下载 URL。
    /// 格式: {cdnBase}/{branchName}/Res/{md5[0]}/{md5}.{filesize}
    /// </summary>
    public string BuildDownloadUrl(string cdnBaseUrl, string branchName)
    {
        string trimmedBase = cdnBaseUrl.TrimEnd('/');
        char firstChar = Md5[0];
        return $"{trimmedBase}/{branchName}/Res/{firstChar}/{Md5}.{Filesize}";
    }
}
