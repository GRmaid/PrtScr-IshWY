namespace PrtScr.Core.Edit;

/// <summary>参与序号推导的标注；0 表示不是序号标注。</summary>
public interface IEditNumbered
{
    /// <summary>序号标注的编号；其他工具为 0。</summary>
    int Number { get; }
}
