using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class SingleInstanceWindowTests
{
    [Fact]
    public void TrySelect_AcceptsHandleOwnedByTheRunningInstance()
    {
        var published = new PublishedInstanceWindow(
            PublishedInstanceWindow.ExpectedMagic,
            ProcessId: 40,
            WindowHandle: 0x1234);

        var selected = SingleInstanceWindowSelector.TrySelect(
            published,
            currentProcessId: 7,
            windowBelongsToProcess: (handle, processId) => handle == 0x1234 && processId == 40,
            out var windowHandle);

        Assert.True(selected);
        Assert.Equal(0x1234, windowHandle);
    }

    [Theory]
    [InlineData(0u, 40u, 0x1234L, 7u)]
    [InlineData(PublishedInstanceWindow.ExpectedMagic, 0u, 0x1234L, 7u)]
    [InlineData(PublishedInstanceWindow.ExpectedMagic, 40u, 0L, 7u)]
    [InlineData(PublishedInstanceWindow.ExpectedMagic, 40u, 0x1234L, 40u)]
    public void TrySelect_RejectsIncompleteOrSelfIdentity(
        uint magic,
        uint processId,
        long windowHandle,
        uint currentProcessId)
    {
        var selected = SingleInstanceWindowSelector.TrySelect(
            new PublishedInstanceWindow(magic, processId, windowHandle),
            currentProcessId,
            windowBelongsToProcess: (_, _) => true,
            out _);

        Assert.False(selected);
    }

    [Fact]
    public void TrySelect_RejectsHandleNoLongerOwnedByPublishedProcess()
    {
        var published = new PublishedInstanceWindow(
            PublishedInstanceWindow.ExpectedMagic,
            ProcessId: 40,
            WindowHandle: 0x1234);

        var selected = SingleInstanceWindowSelector.TrySelect(
            published,
            currentProcessId: 7,
            windowBelongsToProcess: (_, _) => false,
            out var windowHandle);

        Assert.False(selected);
        Assert.Equal(0, windowHandle);
    }

    [Fact]
    public void Payload_RoundTripsLittleEndianIdentity()
    {
        var published = new PublishedInstanceWindow(
            PublishedInstanceWindow.ExpectedMagic,
            ProcessId: 42,
            WindowHandle: unchecked((long)0xFFFF_FFFF_0000_1234));

        Assert.True(PublishedInstanceWindow.TryRead(published.ToBytes(), out var read));
        Assert.Equal(published, read);
        Assert.False(PublishedInstanceWindow.TryRead(new byte[8], out _));
    }

    [Fact]
    public void Publish_RoundTripsHandleWithoutUsingWindowTitle()
    {
        var name = $@"Local\PowerMode.Tests.{Guid.NewGuid():N}";
        using var map = SingleInstanceWindowBridge.Publish(new IntPtr(0x2345), name);

        Assert.True(SingleInstanceWindowBridge.TryRead(out var published, name));
        Assert.Equal(PublishedInstanceWindow.ExpectedMagic, published.Magic);
        Assert.Equal((uint)Environment.ProcessId, published.ProcessId);
        Assert.Equal(0x2345, published.WindowHandle);
        Assert.False(SingleInstanceWindowBridge.TryRead(out _, name + ".missing"));
    }

    [Fact]
    public void AppLaunch_DoesNotFindExistingWindowByTitle()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "App.xaml.cs"));

        Assert.Contains("SingleInstanceWindowBridge.Publish(", source);
        Assert.Contains("SingleInstanceWindowSelector.TrySelect(", source);
        Assert.DoesNotContain("FindWindow", source);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PowerMode.slnx")))
                return Path.Combine([directory.FullName, .. path]);
        }

        throw new DirectoryNotFoundException("Could not locate the PowerMode repository root.");
    }
}
