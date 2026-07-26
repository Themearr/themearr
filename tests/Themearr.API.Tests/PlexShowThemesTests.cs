using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PlexShowThemesTests
{
    // A Plex show-section (/library/sections/{key}/all?type=2) returns shows as <Directory>
    // elements. Each has a <Location path> (the show root folder) and, when Plex has a theme,
    // a `theme` attribute.
    private const string TwoShows = """
        <MediaContainer size="2">
          <Directory ratingKey="45" type="show" title="Breaking Bad" year="2008"
                     theme="/library/metadata/45/theme/1699999999">
            <Location id="1" path="/tv/Breaking Bad" />
          </Directory>
          <Directory ratingKey="46" type="show" title="The Wire" year="2002">
            <Location id="2" path="/tv/The Wire" />
          </Directory>
        </MediaContainer>
        """;

    [Fact]
    public void Parse_reads_root_folder_title_year_and_theme_presence()
    {
        var shows = PlexShowThemes.Parse(TwoShows);

        var bb = shows.Single(s => s.Title == "Breaking Bad");
        Assert.Equal("/tv/Breaking Bad", bb.RootFolder);
        Assert.Equal(2008, bb.Year);
        Assert.True(bb.HasTheme);

        var wire = shows.Single(s => s.Title == "The Wire");
        Assert.Equal("/tv/The Wire", wire.RootFolder);
        Assert.False(wire.HasTheme);
    }

    [Fact]
    public void Parse_uses_the_first_location_when_a_show_has_several()
    {
        const string merged = """
            <MediaContainer size="1">
              <Directory ratingKey="9" type="show" title="Doctor Who" year="2005">
                <Location id="1" path="/tv/Doctor Who (2005)" />
                <Location id="2" path="/tv2/Doctor Who" />
              </Directory>
            </MediaContainer>
            """;
        Assert.Equal("/tv/Doctor Who (2005)", PlexShowThemes.Parse(merged).Single().RootFolder);
    }
}
