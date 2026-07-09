using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage.Tests;

public class SafePathValidatorTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lt-safe-root");

    [Fact]
    public void RelativePathInsideRoot_IsAllowed()
    {
        Assert.True(SafePathValidator.IsInsideRoot(Root, "session.txt"));
        Assert.True(SafePathValidator.IsInsideRoot(Root, Path.Combine("sub", "session.txt")));
    }

    [Fact]
    public void TraversalOutsideRoot_IsBlocked()
    {
        Assert.False(SafePathValidator.IsInsideRoot(Root, Path.Combine("..", "escape.txt")));
        Assert.False(SafePathValidator.IsInsideRoot(Root, Path.Combine("sub", "..", "..", "escape.txt")));
    }

    [Fact]
    public void AbsolutePathOutsideRoot_IsBlocked()
    {
        string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "file.txt");
        Assert.False(SafePathValidator.IsInsideRoot(Root, outside));
    }

    [Fact]
    public void AbsolutePathInsideRoot_IsAllowed()
    {
        string inside = Path.Combine(Root, "file.txt");
        Assert.True(SafePathValidator.IsInsideRoot(Root, inside));
    }

    [Fact]
    public void EmptyInputs_AreBlocked()
    {
        Assert.False(SafePathValidator.IsInsideRoot(Root, ""));
        Assert.False(SafePathValidator.IsInsideRoot("", "x.txt"));
    }

    [Fact]
    public void MidPathTraversal_ThatStaysInside_IsAllowed()
    {
        Assert.True(SafePathValidator.IsInsideRoot(Root, Path.Combine("sub", "..", "file.txt")));
    }

    [Fact]
    public void SiblingFolderWithSimilarPrefix_IsBlocked()
    {
        // /tmp/lt-safe-root vs /tmp/lt-safe-root-evil
        Assert.False(SafePathValidator.IsInsideRoot(Root, Root + "-evil" + Path.DirectorySeparatorChar + "file.txt"));
    }

    [Fact]
    public void OtherDriveAbsolutePath_IsBlocked()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.False(SafePathValidator.IsInsideRoot(Root, @"Z:\outside\file.txt"));
        }
    }

    [Fact]
    public void RootItself_IsAllowed()
    {
        Assert.True(SafePathValidator.IsInsideRoot(Root, Root));
    }
}
