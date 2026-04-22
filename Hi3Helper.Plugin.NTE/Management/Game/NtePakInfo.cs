using System.Collections.Generic;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>
/// 表示 ResList.xml 中 &lt;Package&gt;&lt;Pak&gt; 元素。
/// 代表一个 Pak 归档文件，其内包含多个 Entry 条目。
/// </summary>
internal sealed class NtePakInfo
{
    /// <summary>Pak 归档的 MD5 校验和</summary>
    public string Md5 { get; set; } = string.Empty;

    /// <summary>Pak 归档的总大小（字节）</summary>
    public long Filesize { get; set; }

    /// <summary>Pak 归档内的文件条目列表</summary>
    public List<NtePakEntry> Entries { get; set; } = [];

    /// <summary>
    /// 构建该 Pak 的 CDN 下载 URL。
    /// 格式: {cdnBase}/{branchName}/Res/{md5[0]}/{md5}.{filesize}
    /// </summary>
    public string BuildDownloadUrl(string cdnBaseUrl, string branchName)
    {
        string trimmedBase = cdnBaseUrl.TrimEnd('/');
        char firstChar = Md5[0];
        return $"{trimmedBase}/{branchName}/Res/{firstChar}/{Md5}.{Filesize}";
    }
}
