using PrtScr.Core.Edit;

namespace PrtScr.Core.Tests;

public sealed class EditDocumentTests
{
    private sealed class TestAnnotation : IEditNumbered
    {
        public TestAnnotation(int number = 0, string label = "")
        {
            Number = number;
            Label = label;
        }

        public int Number { get; }

        public string Label { get; }
    }

    private static EditBase NewBase(byte[]? png = null)
        => new(png ?? [1, 2, 3, 4], 640, 480, EditRevision.Initial);

    private static EditDocument<TestAnnotation> NewDocument(byte[]? png = null)
        => EditDocument<TestAnnotation>.Start(NewBase(png));

    // ---------- 不变量 1：基底只在提交成功时替换 ----------

    [Fact]
    public void Start_ExposesBaseAndInitialRevision()
    {
        var editBase = NewBase();
        var doc = EditDocument<TestAnnotation>.Start(editBase);

        Assert.Same(editBase, doc.Base);
        Assert.Equal(EditRevision.Initial, doc.Base.Revision);
        Assert.Equal(0, doc.Count);
        Assert.False(doc.CanUndo);
        Assert.False(doc.HasPendingWork);
        Assert.Equal(1, doc.NextNumber);
    }

    [Fact]
    public void Add_KeepsBaseAndRevision()
    {
        var doc = NewDocument();
        var next = doc.Add(new TestAnnotation(1));

        Assert.Same(doc.Base, next.Base);
        Assert.Equal(EditRevision.Initial, next.Base.Revision);
    }

    [Fact]
    public void WithDraft_KeepsBaseAndRevision()
    {
        var doc = NewDocument();
        var next = doc.WithDraft(new TestAnnotation(0, "draft"));

        Assert.Same(doc.Base, next.Base);
        Assert.Equal(EditRevision.Initial, next.Base.Revision);
    }

    [Fact]
    public void Clear_KeepsBaseAndRevision()
    {
        var doc = NewDocument().Add(new TestAnnotation(1));
        var cleared = doc.Clear();

        Assert.Same(doc.Base, cleared.Base);
        Assert.Equal(EditRevision.Initial, cleared.Base.Revision);
    }

    [Fact]
    public void Undo_KeepsBaseAndRevision()
    {
        var doc = NewDocument().Add(new TestAnnotation(1));
        var undone = doc.Undo();

        Assert.Same(doc.Base, undone.Base);
        Assert.Equal(EditRevision.Initial, undone.Base.Revision);
    }

    [Fact]
    public void Discard_KeepsBaseAndRevision()
    {
        var doc = NewDocument().Add(new TestAnnotation(1)).WithDraft(new TestAnnotation(0, "draft"));
        var discarded = doc.Discard();

        Assert.Same(doc.Base, discarded.Base);
        Assert.Equal(EditRevision.Initial, discarded.Base.Revision);
    }

    [Fact]
    public void Commit_DoesNotMutatePreviousDocument()
    {
        var doc = NewDocument().Add(new TestAnnotation(1));
        var originalBase = doc.Base;
        var committed = doc.Commit([9, 9]);

        Assert.Same(originalBase, doc.Base);
        Assert.Equal(1, doc.Count);
        Assert.True(doc.CanUndo);
        Assert.NotSame(originalBase, committed.Base);
    }

    // ---------- 不变量 2：基底尺寸/字节由提交结果重建 ----------

    [Fact]
    public void Commit_ReusesBasePixelSize()
    {
        var doc = NewDocument();

        var committed = doc.Commit([9, 9]);

        Assert.Equal(640, committed.Base.PixelWidth);
        Assert.Equal(480, committed.Base.PixelHeight);
        Assert.Equal(new byte[] { 9, 9 }, committed.Base.PngBytes);
    }

    [Fact]
    public void Start_NullBase_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EditDocument<TestAnnotation>.Start(null!));
    }

    [Fact]
    public void Add_NullAnnotation_Throws()
    {
        var doc = NewDocument();

        Assert.Throws<ArgumentNullException>(() => doc.Add(null!));
    }

    [Fact]
    public void EditBase_NullPng_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EditBase(null!, 10, 10, EditRevision.Initial));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void EditBase_NonPositiveSize_Throws(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditBase([1], width, height, EditRevision.Initial));
    }

    // ---------- 不变量 3：绘制顺序 = 标注顺序，草稿永远最后 ----------

    [Fact]
    public void GetDrawOrder_WithoutDraft_ReturnsAnnotationsInOrder()
    {
        var a = new TestAnnotation(1);
        var b = new TestAnnotation(2);
        var doc = NewDocument().Add(a).Add(b);

        var order = doc.GetDrawOrder();

        Assert.Equal(2, order.Count);
        Assert.Same(a, order[0]);
        Assert.Same(b, order[1]);
    }

    [Fact]
    public void GetDrawOrder_WithDraft_PutsDraftLast()
    {
        var a = new TestAnnotation(1);
        var draft = new TestAnnotation(0, "draft");
        var doc = NewDocument().Add(a).WithDraft(draft);

        var order = doc.GetDrawOrder();

        Assert.Equal(2, order.Count);
        Assert.Same(a, order[0]);
        Assert.Same(draft, order[1]);
    }

    [Fact]
    public void Draft_IsNeverInAnnotationsNorCounted()
    {
        var draft = new TestAnnotation(0, "draft");
        var doc = NewDocument().WithDraft(draft);

        Assert.Equal(0, doc.Count);
        Assert.Empty(doc.Annotations);
        Assert.Same(draft, doc.Draft);
        Assert.DoesNotContain(doc.Annotations, a => ReferenceEquals(a, draft));
    }

    [Fact]
    public void Add_DoesNotTurnDraftIntoAnnotation()
    {
        var draft = new TestAnnotation(0, "draft");
        var committedAnnotation = new TestAnnotation(1);
        var doc = NewDocument().WithDraft(draft);

        var next = doc.Add(committedAnnotation);

        Assert.Same(draft, next.Draft);
        Assert.Single(next.Annotations);
        Assert.Same(committedAnnotation, next.Annotations[0]);
    }

    // ---------- 不变量 4：序号由现存最大编号 + 1 推导 ----------

    [Fact]
    public void Add_NumberedAnnotation_AdvancesNextNumber()
    {
        var doc = NewDocument();

        var next = doc.Add(new TestAnnotation(1));

        Assert.Equal(2, next.NextNumber);
    }

    [Fact]
    public void Add_NonNumberedAnnotation_KeepsNextNumber()
    {
        var doc = NewDocument();

        var next = doc.Add(new TestAnnotation(0, "rectangle"));

        Assert.Equal(1, next.NextNumber);
    }

    [Fact]
    public void Undo_NumberedAnnotation_ReusesTailNumber()
    {
        var doc = NewDocument()
            .Add(new TestAnnotation(1))
            .Add(new TestAnnotation(2))
            .Add(new TestAnnotation(3));

        Assert.Equal(4, doc.NextNumber);

        var undone = doc.Undo();

        Assert.Equal(2, undone.Count);
        Assert.Equal(3, undone.NextNumber);
    }

    [Fact]
    public void Clear_ResetsNextNumberToOne()
    {
        var doc = NewDocument()
            .Add(new TestAnnotation(1))
            .Add(new TestAnnotation(2));

        var cleared = doc.Clear();

        Assert.Equal(0, cleared.Count);
        Assert.Equal(1, cleared.NextNumber);
    }

    [Fact]
    public void UndoClear_RestoresNextNumber()
    {
        var doc = NewDocument()
            .Add(new TestAnnotation(1))
            .Add(new TestAnnotation(2))
            .Clear();

        var undone = doc.Undo();

        Assert.Equal(2, undone.Count);
        Assert.Equal(3, undone.NextNumber);
    }

    [Fact]
    public void Undo_AllNumberedAnnotations_ReturnsNextNumberOne()
    {
        var doc = NewDocument()
            .Add(new TestAnnotation(1))
            .Add(new TestAnnotation(2))
            .Add(new TestAnnotation(3));

        var undone = doc.Undo().Undo().Undo();

        Assert.Equal(0, undone.Count);
        Assert.Equal(1, undone.NextNumber);
    }

    // ---------- 不变量 5：版本号只在提交时推进 ----------

    [Fact]
    public void NonCommitOperations_KeepInitialRevision()
    {
        var doc = NewDocument()
            .Add(new TestAnnotation(1))
            .WithDraft(new TestAnnotation(0, "draft"))
            .Clear()
            .Undo();

        Assert.Equal(EditRevision.Initial, doc.Base.Revision);
    }

    [Fact]
    public void Commit_AdvancesRevisionByOne()
    {
        var doc = NewDocument();

        var committed = doc.Commit([9]);

        Assert.Equal(EditRevision.Initial.Next(), committed.Base.Revision);
        Assert.Equal(2u, committed.Base.Revision.Value);
    }

    [Fact]
    public void Commit_Twice_IsMonotonic()
    {
        var first = NewDocument().Commit([1]);
        var second = first.Commit([2]);

        Assert.Equal(2u, first.Base.Revision.Value);
        Assert.Equal(3u, second.Base.Revision.Value);
        Assert.True(second.Base.Revision.Value > first.Base.Revision.Value);
    }

    [Fact]
    public void Commit_ClearsAnnotationsDraftAndUndo()
    {
        var doc = NewDocument()
            .Add(new TestAnnotation(1))
            .WithDraft(new TestAnnotation(0, "draft"));

        var committed = doc.Commit([9]);

        Assert.Equal(0, committed.Count);
        Assert.Null(committed.Draft);
        Assert.False(committed.CanUndo);
        Assert.Equal(1, committed.NextNumber);
        Assert.False(committed.HasPendingWork);
    }

    [Fact]
    public void Publish_UsesCurrentBaseRevision()
    {
        var doc = NewDocument().Commit([9]);
        var publishedBytes = new byte[] { 8, 8 };

        var snapshot = doc.Publish(publishedBytes);

        Assert.Same(publishedBytes, snapshot.PngBytes);
        Assert.Equal(doc.Base.Revision, snapshot.Revision);
    }

    [Fact]
    public void Publish_ExposesOnlyPngAndRevision()
    {
        var names = typeof(EditPublishedSnapshot)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "PngBytes", "Revision" }, names);
    }

    [Fact]
    public void Publish_EmptyPng_Throws()
    {
        var doc = NewDocument();

        Assert.Throws<ArgumentException>(() => doc.Publish([]));
    }

    [Fact]
    public void Publish_NullPng_Throws()
    {
        var doc = NewDocument();

        Assert.Throws<ArgumentNullException>(() => doc.Publish(null!));
    }

    // ---------- 不变量 6：提交失败文档不变 ----------

    [Fact]
    public void TryCommit_NullPng_ReturnsNullAndKeepsDocument()
    {
        var draft = new TestAnnotation(0, "draft");
        var doc = NewDocument().Add(new TestAnnotation(1)).WithDraft(draft);

        var committed = doc.TryCommit(null);

        Assert.Null(committed);
        Assert.Equal(1, doc.Count);
        Assert.Same(draft, doc.Draft);
        Assert.Equal(EditRevision.Initial, doc.Base.Revision);
    }

    [Fact]
    public void TryCommit_EmptyPng_ReturnsNullAndKeepsDocument()
    {
        var doc = NewDocument().Add(new TestAnnotation(1));

        var committed = doc.TryCommit([]);

        Assert.Null(committed);
        Assert.Equal(1, doc.Count);
    }

    [Fact]
    public void TryCommit_ValidPng_ReturnsCommittedDocument()
    {
        var doc = NewDocument();

        var committed = doc.TryCommit([9]);

        Assert.NotNull(committed);
        Assert.Equal(2u, committed!.Base.Revision.Value);
        Assert.Equal(0, committed.Count);
    }

    [Fact]
    public void TryCommit_FailureKeepsDraftAndAnnotations()
    {
        var draft = new TestAnnotation(0, "draft");
        var doc = NewDocument().Add(new TestAnnotation(1)).WithDraft(draft);

        Assert.Null(doc.TryCommit(null));

        Assert.Equal(1, doc.Count);
        Assert.Same(draft, doc.Draft);
        Assert.True(doc.CanUndo);
        Assert.True(doc.HasPendingWork);
    }

    // ---------- 不变量 8：取消/关窗不改基底，未提交内容可整体丢弃 ----------

    [Fact]
    public void Discard_ClearsAnnotationsDraftAndUndo()
    {
        var doc = NewDocument()
            .Add(new TestAnnotation(1))
            .WithDraft(new TestAnnotation(0, "draft"));

        var discarded = doc.Discard();

        Assert.Equal(0, discarded.Count);
        Assert.Null(discarded.Draft);
        Assert.False(discarded.CanUndo);
        Assert.False(discarded.HasPendingWork);
    }

    [Fact]
    public void Discard_OnFreshDocument_ReturnsSameInstance()
    {
        var doc = NewDocument();

        Assert.Same(doc, doc.Discard());
    }

    [Fact]
    public void HasPendingWork_TracksAnnotationsAndDraft()
    {
        var doc = NewDocument();
        Assert.False(doc.HasPendingWork);

        var withAnnotation = doc.Add(new TestAnnotation(1));
        Assert.True(withAnnotation.HasPendingWork);

        var withDraft = doc.WithDraft(new TestAnnotation(0, "draft"));
        Assert.True(withDraft.HasPendingWork);
        Assert.Equal(0, withDraft.Count);
    }

    // ---------- 与 NativePinWindow.EditHistory.cs 的行为对照 ----------

    [Fact]
    public void Clear_WhenEmpty_DoesNotPushUndoStep()
    {
        var doc = NewDocument();

        var cleared = doc.Clear();

        Assert.Same(doc, cleared);
        Assert.False(cleared.CanUndo);
    }

    [Fact]
    public void Clear_WithAnnotations_PushesSingleClearOperation()
    {
        var a = new TestAnnotation(1);
        var b = new TestAnnotation(2);
        var doc = NewDocument().Add(a).Add(b);

        var cleared = doc.Clear();

        Assert.Equal(3, cleared.UndoHistory.Count);
        var operation = cleared.UndoHistory[^1];
        Assert.Equal(EditOpKind.Clear, operation.Kind);
        Assert.Null(operation.Annotation);
        Assert.NotNull(operation.Cleared);
        Assert.Equal(2, operation.Cleared!.Count);
        Assert.Same(a, operation.Cleared[0]);
        Assert.Same(b, operation.Cleared[1]);
    }

    [Fact]
    public void Add_RecordsAddOperation()
    {
        var annotation = new TestAnnotation(1);
        var doc = NewDocument().Add(annotation);

        var operation = Assert.Single(doc.UndoHistory);
        Assert.Equal(EditOpKind.Add, operation.Kind);
        Assert.Same(annotation, operation.Annotation);
        Assert.Null(operation.Cleared);
    }

    [Fact]
    public void Undo_OnFreshDocument_IsNoOp()
    {
        var doc = NewDocument();

        var undone = doc.Undo();

        Assert.Same(doc, undone);
        Assert.Empty(undone.UndoHistory);
    }

    [Fact]
    public void Undo_Clear_RestoresAnnotationsInOriginalOrder()
    {
        var a = new TestAnnotation(1);
        var b = new TestAnnotation(2);
        var c = new TestAnnotation(3);
        var cleared = NewDocument().Add(a).Add(b).Add(c).Clear();

        var undone = cleared.Undo();

        Assert.Equal(3, undone.Count);
        Assert.Same(a, undone.Annotations[0]);
        Assert.Same(b, undone.Annotations[1]);
        Assert.Same(c, undone.Annotations[2]);
    }

    [Fact]
    public void Undo_Add_RemovesLastMatchingReference()
    {
        var annotation = new TestAnnotation(1);
        var doc = NewDocument().Add(annotation).Add(annotation);

        var once = doc.Undo();
        var twice = once.Undo();

        Assert.Equal(1, once.Count);
        Assert.Same(annotation, once.Annotations[0]);
        Assert.Equal(0, twice.Count);
    }

    [Fact]
    public void Undo_Sequence_UnwindsEverything()
    {
        var a = new TestAnnotation(1);
        var b = new TestAnnotation(2);
        var c = new TestAnnotation(3);
        var doc = NewDocument().Add(a).Add(b).Clear().Add(c);

        Assert.Equal(1, doc.Count);
        Assert.Equal(4, doc.NextNumber);

        var afterAdd = doc.Undo();
        Assert.Equal(0, afterAdd.Count);
        Assert.Equal(1, afterAdd.NextNumber);

        var afterClear = afterAdd.Undo();
        Assert.Equal(2, afterClear.Count);
        Assert.Equal(3, afterClear.NextNumber);

        var afterSecond = afterClear.Undo();
        Assert.Equal(1, afterSecond.Count);
        Assert.Equal(2, afterSecond.NextNumber);

        var afterFirst = afterSecond.Undo();
        Assert.Equal(0, afterFirst.Count);
        Assert.False(afterFirst.CanUndo);
        Assert.Equal(1, afterFirst.NextNumber);
    }

    [Fact]
    public void Start_AfterEditing_StartsFreshHistory()
    {
        var edited = NewDocument().Add(new TestAnnotation(1)).Clear();

        var restarted = EditDocument<TestAnnotation>.Start(edited.Commit([9]).Base);

        Assert.False(restarted.CanUndo);
        Assert.Equal(0, restarted.Count);
        Assert.Equal(1, restarted.NextNumber);
    }

    [Fact]
    public void Count_CountsVisibleAnnotationsOnly()
    {
        var doc = NewDocument()
            .Add(new TestAnnotation(1))
            .Add(new TestAnnotation(2))
            .WithDraft(new TestAnnotation(0, "draft"));

        Assert.Equal(2, doc.Count);
    }
}
