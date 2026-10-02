using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

[Trait("Category", "Viewer")]
public sealed class PdfPageCursorTests
{
    [Fact]
    public void Unknown_Document_CannotStep()
    {
        var cursor = new PdfPageCursor();

        Assert.False(cursor.TryStep(+1));
        Assert.False(cursor.TryStep(-1));
        Assert.Equal(0, cursor.Index);
        Assert.False(cursor.IsMultiPage);
    }

    [Fact]
    public void SinglePage_CannotStep()
    {
        var cursor = new PdfPageCursor();
        cursor.SetCount(1);

        Assert.False(cursor.TryStep(+1));
        Assert.False(cursor.IsMultiPage);
    }

    [Fact]
    public void Steps_Within_TheDocument_AndStopsAtBothEnds()
    {
        var cursor = new PdfPageCursor();
        cursor.SetCount(3);

        Assert.False(cursor.TryStep(-1)); // first page: no previous
        Assert.True(cursor.TryStep(+1));
        Assert.True(cursor.TryStep(+1));
        Assert.Equal(2, cursor.Index);
        Assert.False(cursor.TryStep(+1)); // last page: no wrap-around
        Assert.Equal(2, cursor.Index);
        Assert.True(cursor.TryStep(-1));
        Assert.Equal(1, cursor.Index);
    }

    [Fact]
    public void ZeroDelta_IsNotAStep()
    {
        var cursor = new PdfPageCursor();
        cursor.SetCount(3);

        Assert.False(cursor.TryStep(0));
    }

    [Fact]
    public void SetCount_KeepsIndexInsideTheDocument()
    {
        var cursor = new PdfPageCursor();
        cursor.SetCount(5);
        cursor.TryStep(+4);

        cursor.SetCount(2); // the file changed and is shorter now

        Assert.Equal(1, cursor.Index);
    }

    [Fact]
    public void Reset_GoesBackToTheFirstPageOfAnUnknownDocument()
    {
        var cursor = new PdfPageCursor();
        cursor.SetCount(4);
        cursor.TryStep(+2);

        cursor.Reset();

        Assert.Equal(0, cursor.Index);
        Assert.Equal(0, cursor.Count);
    }
}
