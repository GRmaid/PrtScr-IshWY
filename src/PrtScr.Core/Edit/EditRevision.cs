namespace PrtScr.Core.Edit;

/// <summary>
/// 编辑基底的版本号：新基底从 <see cref="Initial"/> 开始，只在提交成功时由 <see cref="Next"/> 推进。
/// 其余操作（新增标注、撤销、清空、取消）都不改变版本号。
/// </summary>
public readonly record struct EditRevision(uint Value)
{
    /// <summary>首个基底的版本号。</summary>
    public static EditRevision Initial => new(1);

    /// <summary>提交成功后的下一个版本号；只前进，不回退。</summary>
    public EditRevision Next() => new(Value + 1);

    public override string ToString() => Value.ToString();
}
