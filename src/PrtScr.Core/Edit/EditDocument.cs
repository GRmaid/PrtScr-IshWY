namespace PrtScr.Core.Edit;

/// <summary>
/// 标注编辑文档（不可变）：基底 + 当前可见标注（按绘制顺序）+ 草稿 + 撤销历史。
/// 所有修改都返回新实例，原实例保持不变；基底只由 <see cref="Commit"/> 替换，因此
/// 「编辑期只有提交成功才写基底」是类型层的不变量，而不是靠调用方自觉。
/// 本类型位于 Core，不引用 System.Drawing / WinUI；标注负载由 <typeparamref name="TAnnotation"/> 提供。
/// </summary>
public sealed class EditDocument<TAnnotation>
    where TAnnotation : class, IEditNumbered
{
    private readonly TAnnotation[] _annotations;
    private readonly EditOperation<TAnnotation>[] _operations;

    private EditDocument(
        EditBase editBase,
        TAnnotation[] annotations,
        TAnnotation? draft,
        EditOperation<TAnnotation>[] operations)
    {
        Base = editBase;
        _annotations = annotations;
        _operations = operations;
        Draft = draft;
        Annotations = Array.AsReadOnly(annotations);
        UndoHistory = Array.AsReadOnly(operations);
        NextNumber = EditNumbering.NextAfter(CollectNumbers(annotations));
    }

    /// <summary>当前基底；除提交外所有操作都保持同一个实例。</summary>
    public EditBase Base { get; }

    /// <summary>当前可见标注，顺序即绘制顺序；草稿不在这个列表里。</summary>
    public IReadOnlyList<TAnnotation> Annotations { get; }

    /// <summary>正在绘制的草稿；抬笔时由调用方转成 <see cref="Add"/>，草稿本身永不进入 <see cref="Annotations"/>。</summary>
    public TAnnotation? Draft { get; }

    /// <summary>撤销栈，最早在前、最新在后；只读快照，由文档内部维护。</summary>
    public IReadOnlyList<EditOperation<TAnnotation>> UndoHistory { get; }

    /// <summary>序号工具的下一个编号；由现存标注的最大编号 + 1 推导，撤销后尾号自动可复用。</summary>
    public int NextNumber { get; }

    /// <summary>当前可见标注数量（不含草稿）。</summary>
    public int Count => _annotations.Length;

    /// <summary>是否还有可撤销的操作。</summary>
    public bool CanUndo => _operations.Length > 0;

    /// <summary>是否有未提交内容（标注或草稿）；关窗/取消前据此决定是否要用户确认。</summary>
    public bool HasPendingWork => _annotations.Length > 0 || Draft is not null;

    /// <summary>开启一次新的编辑会话；标注、草稿与撤销历史都从空开始（重入编辑不继承旧会话）。</summary>
    public static EditDocument<TAnnotation> Start(EditBase editBase)
    {
        ArgumentNullException.ThrowIfNull(editBase);
        return new EditDocument<TAnnotation>(editBase, [], null, []);
    }

    /// <summary>
    /// 新增一条标注并记录一步撤销。序号标注的 <see cref="IEditNumbered.Number"/> 必须在调用前设为当前
    /// <see cref="NextNumber"/>（与视图层 ++_numberCounter 的分工一致）。
    /// </summary>
    public EditDocument<TAnnotation> Add(TAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        var annotations = new TAnnotation[_annotations.Length + 1];
        Array.Copy(_annotations, annotations, _annotations.Length);
        annotations[^1] = annotation;

        var operations = new EditOperation<TAnnotation>[_operations.Length + 1];
        Array.Copy(_operations, operations, _operations.Length);
        operations[^1] = EditOperation<TAnnotation>.Add(annotation);

        return new EditDocument<TAnnotation>(Base, annotations, Draft, operations);
    }

    /// <summary>替换草稿；草稿不进入撤销栈，也不改变 <see cref="NextNumber"/>。</summary>
    public EditDocument<TAnnotation> WithDraft(TAnnotation? draft)
        => new(Base, _annotations, draft, _operations);

    /// <summary>清空全部标注（可撤销）；没有标注时返回当前实例，不记录撤销步。</summary>
    public EditDocument<TAnnotation> Clear()
    {
        if (_annotations.Length == 0)
            return this;

        var operations = new EditOperation<TAnnotation>[_operations.Length + 1];
        Array.Copy(_operations, operations, _operations.Length);
        operations[^1] = EditOperation<TAnnotation>.Clear(Annotations);

        return new EditDocument<TAnnotation>(Base, [], Draft, operations);
    }

    /// <summary>撤销最后一步：Add 移除对应标注，Clear 恢复清空前的快照；没有可撤销操作时返回当前实例。</summary>
    public EditDocument<TAnnotation> Undo()
    {
        if (_operations.Length == 0)
            return this;

        var entry = _operations[^1];
        var operations = new EditOperation<TAnnotation>[_operations.Length - 1];
        Array.Copy(_operations, operations, operations.Length);

        var annotations = entry.Kind == EditOpKind.Clear
            ? Copy(entry.Cleared)
            : RemoveLastReference(entry.Annotation);

        return new EditDocument<TAnnotation>(Base, annotations, Draft, operations);
    }

    /// <summary>提交成功：用合成后的 PNG 换基底并推进版本号；标注、草稿与撤销历史清空。</summary>
    public EditDocument<TAnnotation> Commit(byte[] composedPng)
    {
        ArgumentNullException.ThrowIfNull(composedPng);
        if (composedPng.Length == 0)
            throw new ArgumentException("提交的 PNG 不能为空。", nameof(composedPng));

        var nextBase = new EditBase(composedPng, Base.PixelWidth, Base.PixelHeight, Base.Revision.Next());
        return new EditDocument<TAnnotation>(nextBase, [], null, []);
    }

    /// <summary>
    /// 提交失败路径：合成结果为 null 或空时返回 null，原文档（含标注与草稿）保持不变。
    /// 调用方据此提示用户并留在编辑态。
    /// </summary>
    public EditDocument<TAnnotation>? TryCommit(byte[]? composedPng)
        => composedPng is { Length: > 0 } ? Commit(composedPng) : null;

    /// <summary>取消/确认关窗后丢弃未提交内容：基底与版本号不变，标注、草稿、撤销历史清空。</summary>
    public EditDocument<TAnnotation> Discard()
        => _annotations.Length == 0 && Draft is null && _operations.Length == 0
            ? this
            : new EditDocument<TAnnotation>(Base, [], null, []);

    /// <summary>绘制顺序：先按顺序画可见标注，草稿永远最后；草稿不会出现在 <see cref="Annotations"/> 里。</summary>
    public IReadOnlyList<TAnnotation> GetDrawOrder()
    {
        if (Draft is null)
            return Annotations;

        var order = new TAnnotation[_annotations.Length + 1];
        Array.Copy(_annotations, order, _annotations.Length);
        order[^1] = Draft;
        return Array.AsReadOnly(order);
    }

    /// <summary>
    /// 生成跨线程发布用的最小快照（PNG + 当前基底版本号）；UI 线程只比较版本号与字节，
    /// 不接触标注、位图或本文档实例。
    /// </summary>
    public EditPublishedSnapshot Publish(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
            throw new ArgumentException("发布的 PNG 不能为空。", nameof(pngBytes));

        return new EditPublishedSnapshot(pngBytes, Base.Revision);
    }

    private static int[] CollectNumbers(TAnnotation[] annotations)
    {
        var numbers = new int[annotations.Length];
        for (var i = 0; i < annotations.Length; i++)
            numbers[i] = annotations[i].Number;
        return numbers;
    }

    private static TAnnotation[] Copy(IReadOnlyList<TAnnotation>? source)
    {
        if (source is null || source.Count == 0)
            return [];

        var copy = new TAnnotation[source.Count];
        for (var i = 0; i < source.Count; i++)
            copy[i] = source[i];
        return copy;
    }

    private TAnnotation[] RemoveLastReference(TAnnotation? annotation)
    {
        if (annotation is null)
            return _annotations;

        var index = -1;
        for (var i = _annotations.Length - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_annotations[i], annotation))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return _annotations;

        var annotations = new TAnnotation[_annotations.Length - 1];
        Array.Copy(_annotations, 0, annotations, 0, index);
        Array.Copy(_annotations, index + 1, annotations, index, _annotations.Length - index - 1);
        return annotations;
    }
}
