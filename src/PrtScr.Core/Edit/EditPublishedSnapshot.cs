namespace PrtScr.Core.Edit;

/// <summary>
/// 跨线程发布的最小快照：只有提交后的 PNG 与版本号，不含标注、位图或文档实例。
/// UI 线程按 <see cref="Revision"/> 判断结果是否过期。
/// </summary>
public sealed record EditPublishedSnapshot(byte[] PngBytes, EditRevision Revision);
