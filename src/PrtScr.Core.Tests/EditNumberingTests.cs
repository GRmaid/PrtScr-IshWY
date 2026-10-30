using PrtScr.Core.Edit;

namespace PrtScr.Core.Tests;

public sealed class EditNumberingTests
{
    [Theory]
    [InlineData(new int[] { }, 1)]
    [InlineData(new[] { 1 }, 2)]
    [InlineData(new[] { 1, 2, 3 }, 4)]
    [InlineData(new[] { 1, 2 }, 3)]
    [InlineData(new[] { 3, 7 }, 8)]
    [InlineData(new[] { 0, 0 }, 1)]
    [InlineData(new[] { -5, 2 }, 3)]
    [InlineData(new[] { 1, 2, 2, 3 }, 4)]
    public void NextAfter_ReturnsMaximumPlusOne(int[] numbers, int expected)
    {
        Assert.Equal(expected, EditNumbering.NextAfter(numbers));
    }

    [Fact]
    public void NextAfter_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EditNumbering.NextAfter(null!));
    }

    [Fact]
    public void Initial_IsOne()
    {
        Assert.Equal(1u, EditRevision.Initial.Value);
    }

    [Fact]
    public void Next_IncrementsByOne()
    {
        var next = EditRevision.Initial.Next();

        Assert.Equal(2u, next.Value);
    }

    [Fact]
    public void Next_Repeated_IsMonotonic()
    {
        var revision = EditRevision.Initial;
        var values = new List<uint>();
        for (var i = 0; i < 4; i++)
        {
            values.Add(revision.Value);
            revision = revision.Next();
        }

        Assert.Equal(new uint[] { 1, 2, 3, 4 }, values);
    }

    [Fact]
    public void EditRevision_EqualityIsByValue()
    {
        Assert.Equal(new EditRevision(3), new EditRevision(3));
        Assert.NotEqual(new EditRevision(3), new EditRevision(4));
    }
}
