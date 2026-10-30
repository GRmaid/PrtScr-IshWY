namespace PrtScr.Core.Edit;

/// <summary>可撤销编辑操作的类型。</summary>
public enum EditOpKind
{
    Add,
    Clear,
}

/// <summary>
/// 一步可撤销的编辑操作（不可变快照）：Add 记录新增的标注；Clear 记录清空前的全部标注。
/// </summary>
public sealed record EditOperation<TAnnotation>
    where TAnnotation : class
{
    private EditOperation(EditOpKind kind, TAnnotation? annotation, IReadOnlyList<TAnnotation>? cleared)
    {
        Kind = kind;
        Annotation = annotation;
        Cleared = cleared;
    }

    /// <summary>操作类型。</summary>
    public EditOpKind Kind { get; }

    /// <summary>Add 时为新增的标注；Clear 时为 null。</summary>
    public TAnnotation? Annotation { get; }

    /// <summary>Clear 时为被清空前的标注快照（原绘制顺序）；Add 时为 null。</summary>
    public IReadOnlyList<TAnnotation>? Cleared { get; }

    /// <summary>记录一次新增。</summary>
    public static EditOperation<TAnnotation> Add(TAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return new EditOperation<TAnnotation>(EditOpKind.Add, annotation, null);
    }

    /// <summary>记录一次清空，并保存被清空标注的只读快照，供撤销恢复。</summary>
    public static EditOperation<TAnnotation> Clear(IReadOnlyList<TAnnotation> cleared)
    {
        ArgumentNullException.ThrowIfNull(cleared);

        var snapshot = new TAnnotation[cleared.Count];
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i] = cleared[i];

        return new EditOperation<TAnnotation>(EditOpKind.Clear, null, Array.AsReadOnly(snapshot));
    }
}
