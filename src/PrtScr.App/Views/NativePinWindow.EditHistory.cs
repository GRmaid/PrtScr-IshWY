using PrtScr.App.Services;
using PrtScr.Core.Edit;

namespace PrtScr.App.Views;

/// <summary>
/// 标注文档的唯一状态来源：可见标注序列、草稿、撤销历史与下一个序号都由 Core 的
/// <see cref="EditDocument{TAnnotation}"/> 维护。本文件只做视图层适配（把 <c>_annotations</c> /
/// <c>_draft</c> / <c>_numberCounter</c> / <c>CanUndo</c> 映射到 Core 调用），不再保留第二套历史语义。
/// </summary>
public sealed unsafe partial class NativePinWindow
{
    private EditDocument<Annotation>? _document;

    /// <summary>可见标注（绘制顺序）。无文档时为空序列，避免视图层出现 null 分支。</summary>
    private IReadOnlyList<Annotation> _annotations => _document?.Annotations ?? Array.Empty<Annotation>();

    /// <summary>正在画的一笔；Core 保证它永远不会进入 <see cref="EditDocument{TAnnotation}.Annotations"/>。</summary>
    private Annotation? _draft => _document?.Draft;

    /// <summary>下一个序号 = 现存最大序号 + 1；清空后回到 1，撤销后回收尾号。</summary>
    private int _numberCounter => _document?.NextNumber ?? 1;

    private bool CanUndo => _document?.CanUndo == true;

    private bool HasPendingAnnotations => _document?.HasPendingWork == true;

    /// <summary>进入标注：以当前已提交 PNG 与发布版本作为新文档基底，撤销历史为空。</summary>
    private void BeginEditDocument()
    {
        _document = EditDocument<Annotation>.Start(
            new EditBase(_pngBytes, _pixelWidth, _pixelHeight, _publishedRevision));
    }

    /// <summary>离开标注：丢弃未提交的文档状态；已提交版本由 <c>_publishedRevision</c> 承接。</summary>
    private void EndEditDocument() => _document = null;

    /// <summary>新增标注并记一步撤销；所有新增点都必须走这里，保证撤销历史完整。</summary>
    private void AddAnnotation(Annotation annotation)
    {
        if (_document is null)
        {
            App.Log("Pin: add annotation ignored (no edit document)");
            return;
        }

        _document = _document.Add(annotation);
    }

    /// <summary>设置/清除草稿（正在画的一笔），草稿不进入撤销历史。</summary>
    private void SetDraft(Annotation? draft)
    {
        if (_document is null)
            return;

        _document = _document.WithDraft(draft);
    }

    /// <summary>清空全部标注（可撤销）：清空本身作为一步进入撤销历史，序号回到 1。</summary>
    private void ClearAnnotations()
    {
        if (_document is null)
            return;

        _document = _document.Clear();
    }

    /// <summary>撤销最后一步；撤销后下一个序号按现存最大序号重算，尾号可复用。</summary>
    private void UndoEdit()
    {
        if (_document is null)
        {
            RenderEditMode();
            return;
        }

        _document = _document.Undo();
        App.Log($"Pin: undo remaining={_document.UndoHistory.Count} annotations={_document.Count}");
        RenderEditMode();
    }
}
