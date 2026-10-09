namespace HistoryExemplum;

/// <summary>模块名、指令域与页面身份的唯一权威源。</summary>
internal static class ExemplumIdentity
{
    /// <summary>模块名：部署槽、manifest 与 <see cref="ModuleInfo.ModuleName"/>。</summary>
    public const string Name = "HistoryExemplum";

    /// <summary>指令域：全部指令形如 <c>exemplum.&lt;类&gt;.&lt;方法&gt;</c>。</summary>
    public const string Domain = "exemplum";

    /// <summary>
    /// 页面 owner 与场景 id。Aurora 按指令域反推（<c>"History" + 首字母大写的域</c>），
    /// 与模块名无关；对不上整页被静默拒收，只留一条 Warn。合同检查守着这条。
    /// </summary>
    public const string PageOwner = "HistoryExemplum";

    /// <summary>用户看到的页面标题。</summary>
    public const string PageTitle = "Exemplum";

    /// <summary>模块一句话描述，与 module.manifest.json 的 description 保持一致。</summary>
    public const string Description = "{{模块一句话描述}}";
}
