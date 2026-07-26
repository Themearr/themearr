using Themearr.API.Services;

namespace Themearr.API.Tests;

public class MediaFolderIdTests
{
    [Fact]
    public void Is_stable_across_calls()
    {
        Assert.Equal(MediaFolderId.For("/movies/Heat (1995)"), MediaFolderId.For("/movies/Heat (1995)"));
    }

    [Fact]
    public void Is_sixteen_lowercase_hex_characters()
    {
        var id = MediaFolderId.For("/movies/Heat (1995)");

        Assert.Equal(16, id.Length);
        Assert.Matches("^[0-9a-f]{16}$", id);
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_id()
    {
        Assert.Equal(MediaFolderId.For("/movies/Heat (1995)"), MediaFolderId.For("/movies/Heat (1995)/"));
    }

    [Fact]
    public void Different_folders_get_different_ids()
    {
        Assert.NotEqual(MediaFolderId.For("/movies/Heat (1995)"), MediaFolderId.For("/movies/Ronin (1998)"));
    }

    [Fact]
    public void Case_is_significant_because_linux_paths_are()
    {
        Assert.NotEqual(MediaFolderId.For("/movies/heat (1995)"), MediaFolderId.For("/movies/Heat (1995)"));
    }

    [Fact]
    public void An_empty_folder_yields_an_empty_id()
    {
        Assert.Equal("", MediaFolderId.For(""));
    }
}
