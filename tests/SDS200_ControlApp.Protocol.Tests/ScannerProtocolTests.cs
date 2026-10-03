using SDS200_ControlApp.Protocol;

namespace SDS200_ControlApp.Protocol.Tests;

public class ScannerProtocolTests
{
    [Fact]
    public void Frame_AddsCarriageReturn()
    {
        Assert.Equal("MDL\r", ScannerCommands.Frame("MDL"));
    }

    [Fact]
    public void Frame_DoesNotDuplicateExistingTerminator()
    {
        Assert.Equal("MDL\r", ScannerCommands.Frame("MDL\r"));
    }

    [Theory]
    [InlineData("MDL\rVER")]
    [InlineData("MDL\nVER")]
    [InlineData("MDL\r\n")]
    [InlineData("MDL,雪")]
    [InlineData("MDL,\t")]
    public void Frame_RejectsEmbeddedOrMultipleLineTerminators(string command)
    {
        Assert.Throws<ArgumentException>(() => ScannerCommands.Frame(command));
    }

    [Fact]
    public void TryParseModel_RecognizesSds200()
    {
        var parsed = ScannerCommands.TryParseModel("MDL,SDS200\r", out var model);

        Assert.True(parsed);
        Assert.Equal("SDS200", model);
    }

    [Fact]
    public void TryParseFirmware_ReturnsFirmwareText()
    {
        var parsed = ScannerCommands.TryParseFirmware("VER,Version 1.02.03\r", out var firmware);

        Assert.True(parsed);
        Assert.Equal("Version 1.02.03", firmware);
    }

    [Fact]
    public void TryParseModel_RejectsOtherModels()
    {
        var parsed = ScannerCommands.TryParseModel("VER,Version 1.02.03\r", out var model);

        Assert.False(parsed);
        Assert.Null(model);
    }

    [Theory]
    [InlineData("MDL,\r")]
    [InlineData("MDL,   \r")]
    [InlineData("MDL,SDS100,EXTRA\r")]
    public void TryParseModel_RejectsEmptyOrMalformedIdentifiers(string response)
    {
        Assert.False(ScannerCommands.TryParseModel(response, out var model));
        Assert.Null(model);
    }

    [Fact]
    public void BuildStatusPush_FramesIntervalCommand()
    {
        Assert.Equal("PSI,1000\r", ScannerCommands.BuildStatusPush(1000));
    }

    [Fact]
    public void BuildStartScanCommand_UsesTopChannelSentinel()
    {
        Assert.Equal("JPM,SCN_MODE,4294967295\r", ScannerCommands.BuildStartScanCommand());
    }

    [Fact]
    public void BuildPowerOffCommand_FramesDocumentedCommand()
    {
        Assert.Equal("POF\r", ScannerCommands.BuildPowerOffCommand());
    }

    [Fact]
    public void RecordingCommands_FrameQueryAndStartStopValues()
    {
        Assert.Equal("URC\r", ScannerCommands.BuildGetRecordingStatusCommand());
        Assert.Equal("URC,1\r", ScannerCommands.BuildSetRecordingStatusCommand(true));
        Assert.Equal("URC,0\r", ScannerCommands.BuildSetRecordingStatusCommand(false));
    }

    [Theory]
    [InlineData("URC,0\r", 0, null, false)]
    [InlineData("URC,1\r", 1, null, false)]
    [InlineData("URC,OK\r", null, null, true)]
    [InlineData("URC,ERR,0002\r", null, "0002", false)]
    public void TryParseRecordingResponse_ParsesDocumentedReplies(string response, int? status, string? errorCode, bool isAcknowledgement)
    {
        Assert.True(ScannerCommands.TryParseRecordingResponse(response, out var parsed));
        Assert.Equal(status, parsed!.Status);
        Assert.Equal(errorCode, parsed.ErrorCode);
        Assert.Equal(isAcknowledgement, parsed.IsAcknowledgement);
    }

    [Theory]
    [InlineData("URC,2\r")]
    [InlineData("URC,ERR\r")]
    [InlineData("VOL,1\r")]
    public void TryParseRecordingResponse_RejectsMalformedReplies(string response)
    {
        Assert.False(ScannerCommands.TryParseRecordingResponse(response, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void BuildHoldCommand_UsesProvidedTargetAndTags()
    {
        Assert.Equal("HLD,CHANNEL,1,2\r", ScannerCommands.BuildHoldCommand("CHANNEL", "1", "2"));
    }

    [Fact]
    public void BuildNextCommand_UsesPositiveCount()
    {
        Assert.Equal("NXT,CHANNEL,1,2,3\r", ScannerCommands.BuildNextCommand("CHANNEL", "1", "2", 3));
    }

    [Fact]
    public void BuildPreviousCommand_UsesPositiveCount()
    {
        Assert.Equal("PRV,CHANNEL,1,2,3\r", ScannerCommands.BuildPreviousCommand("CHANNEL", "1", "2", 3));
    }

    [Fact]
    public void BuildJumpToTagCommand_UsesProvidedTags()
    {
        Assert.Equal("JNT,1,2,3\r", ScannerCommands.BuildJumpToTagCommand("1", "2", "3"));
    }

    [Theory]
    [InlineData(1, "AVD,CHANNEL,1,2,1\r")]
    [InlineData(2, "AVD,CHANNEL,1,2,2\r")]
    [InlineData(3, "AVD,CHANNEL,1,2,3\r")]
    public void BuildSetAvoidCommand_FormatsDocumentedActions(int status, string expected)
    {
        Assert.Equal(expected, ScannerCommands.BuildSetAvoidCommand("CHANNEL", "1", "2", status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void BuildSetAvoidCommand_RejectsUnsupportedActions(int status)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildSetAvoidCommand("CHANNEL", "1", "2", status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NavigationCommands_RejectNonPositiveCount(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildNextCommand("CHANNEL", "1", "2", count));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildPreviousCommand("CHANNEL", "1", "2", count));
    }

    [Fact]
    public void NavigationCommands_RejectFieldsThatBreakFraming()
    {
        Assert.Throws<ArgumentException>(() => ScannerCommands.BuildHoldCommand("CHANNEL,OTHER", "1", "2"));
        Assert.Throws<ArgumentException>(() => ScannerCommands.BuildJumpToTagCommand("1\r", "2", "3"));
    }

    [Fact]
    public void BuildGetFavoritesListQuickKeysCommand_FramesQuery()
    {
        Assert.Equal("FQK\r", ScannerCommands.BuildGetFavoritesListQuickKeysCommand());
    }

    [Fact]
    public void BuildGetFavoritesListsCommand_FramesGltFavoritesListQuery()
    {
        Assert.Equal("GLT,FL\r", ScannerCommands.BuildGetFavoritesListsCommand());
    }

    [Theory]
    [InlineData("GLT,SYS,7\r", 7)]
    [InlineData("GLT,DEPT,12\r", 12)]
    [InlineData("GLT,SITE,12\r", 12)]
    [InlineData("GLT,CFREQ,3\r", 3)]
    [InlineData("GLT,TGID,3\r", 3)]
    [InlineData("GLT,SFREQ,5\r", 5)]
    public void BuildGltChildListCommands_UseDocumentedTypeAndRuntimeIndex(string expected, int index)
    {
        var command = expected.Split(',')[1] switch
        {
            "SYS" => ScannerCommands.BuildGetSystemsCommand(index),
            "DEPT" => ScannerCommands.BuildGetDepartmentsCommand(index),
            "SITE" => ScannerCommands.BuildGetSitesCommand(index),
            "CFREQ" => ScannerCommands.BuildGetConventionalFrequenciesCommand(index),
            "TGID" => ScannerCommands.BuildGetTalkgroupsCommand(index),
            "SFREQ" => ScannerCommands.BuildGetSiteFrequenciesCommand(index),
            _ => throw new InvalidOperationException("Unexpected GLT type in test case.")
        };

        Assert.Equal(expected, command);
    }

    [Fact]
    public void BuildGltChildListCommands_RejectNegativeRuntimeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildGetSystemsCommand(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildGetDepartmentsCommand(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildGetSitesCommand(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildGetConventionalFrequenciesCommand(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildGetTalkgroupsCommand(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildGetSiteFrequenciesCommand(-1));
    }

    [Fact]
    public void ScannerXmlTreeParser_PreservesElementNamesAttributesAndText()
    {
        const string xml = "<GLT><FavoritesList index=\"4\"><Name>County</Name></FavoritesList></GLT>\r";

        var root = ScannerXmlTreeParser.Parse(xml);

        Assert.Equal("<GLT>", root.Label);
        Assert.Equal("<FavoritesList @index=4>", root.Children[0].Label);
        Assert.Equal("<Name> County", root.Children[0].Children[0].Label);
    }

    [Fact]
    public void BuildSetFavoritesListQuickKeysCommand_RequiresExactlyOneHundredValidStatuses()
    {
        var statuses = Enumerable.Repeat(2, ScannerCommands.FavoritesListQuickKeyCount);

        var command = ScannerCommands.BuildSetFavoritesListQuickKeysCommand(statuses);

        Assert.Equal($"FQK,{string.Join(',', statuses)}\r", command);
    }

    [Fact]
    public void BuildSetFavoritesListQuickKeysCommand_RejectsWrongNumberOfStatuses()
    {
        Assert.Throws<ArgumentException>(() => ScannerCommands.BuildSetFavoritesListQuickKeysCommand([0, 1, 2]));
    }

    [Fact]
    public void BuildSetFavoritesListQuickKeysCommand_RejectsInvalidStatus()
    {
        var statuses = Enumerable.Repeat(0, ScannerCommands.FavoritesListQuickKeyCount).ToArray();
        statuses[^1] = 3;

        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildSetFavoritesListQuickKeysCommand(statuses));
    }

    [Fact]
    public void TryParseFavoritesListQuickKeys_ParsesOneHundredStatuses()
    {
        var response = $"FQK,{string.Join(',', Enumerable.Range(0, ScannerCommands.FavoritesListQuickKeyCount).Select(index => index % 3))}\r";

        var parsed = ScannerCommands.TryParseFavoritesListQuickKeys(response, out var statuses);

        Assert.True(parsed);
        Assert.Equal(Enumerable.Range(0, ScannerCommands.FavoritesListQuickKeyCount).Select(index => index % 3), statuses);
    }

    [Theory]
    [InlineData("FQK,0,1,2\r")]
    [InlineData("FQK,0,1,3\r")]
    public void TryParseFavoritesListQuickKeys_RejectsInvalidResponse(string response)
    {
        Assert.False(ScannerCommands.TryParseFavoritesListQuickKeys(response, out var statuses));
        Assert.Empty(statuses);
    }

    [Fact]
    public void BuildSystemQuickKeyCommands_IncludeFavoritesListIndex()
    {
        Assert.Equal("SQK,7\r", ScannerCommands.BuildGetSystemQuickKeysCommand(7));
        Assert.Equal($"SQK,7,{string.Join(',', Enumerable.Repeat(2, 100))}\r",
            ScannerCommands.BuildSetSystemQuickKeysCommand(7, Enumerable.Repeat(2, 100)));
    }

    [Fact]
    public void TryParseSystemQuickKeys_RequiresMatchingFavoritesListIndex()
    {
        var statuses = Enumerable.Range(0, 100).Select(index => index % 3).ToArray();
        var response = $"SQK,7,{string.Join(',', statuses)}\r";

        Assert.True(ScannerCommands.TryParseSystemQuickKeys(response, 7, out var parsedStatuses));
        Assert.Equal(statuses, parsedStatuses);
        Assert.False(ScannerCommands.TryParseSystemQuickKeys(response, 8, out _));
    }

    [Fact]
    public void BuildDepartmentQuickKeyCommands_IncludeFavoritesListAndSystemIndices()
    {
        Assert.Equal("DQK,7,12\r", ScannerCommands.BuildGetDepartmentQuickKeysCommand(7, 12));
        Assert.Equal($"DQK,7,12,{string.Join(',', Enumerable.Repeat(1, 100))}\r",
            ScannerCommands.BuildSetDepartmentQuickKeysCommand(7, 12, Enumerable.Repeat(1, 100)));
    }

    [Fact]
    public void TryParseDepartmentQuickKeys_RequiresMatchingParentIndices()
    {
        var statuses = Enumerable.Range(0, 100).Select(index => index % 3).ToArray();
        var response = $"DQK,7,12,{string.Join(',', statuses)}\r";

        Assert.True(ScannerCommands.TryParseDepartmentQuickKeys(response, 7, 12, out var parsedStatuses));
        Assert.Equal(statuses, parsedStatuses);
        Assert.False(ScannerCommands.TryParseDepartmentQuickKeys(response, 7, 13, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void HierarchicalQuickKeyCommands_RejectOutOfRangeParentIndices(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildGetSystemQuickKeysCommand(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildGetDepartmentQuickKeysCommand(index, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildGetDepartmentQuickKeysCommand(0, index));
    }

    [Theory]
    [InlineData(0, "VOL,0\r")]
    [InlineData(29, "VOL,29\r")]
    public void BuildVolumeCommand_FramesSupportedLevels(int level, string expected)
    {
        Assert.Equal(expected, ScannerCommands.BuildVolumeCommand(level));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(30)]
    public void BuildVolumeCommand_RejectsUnsupportedLevels(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildVolumeCommand(level));
    }

    [Theory]
    [InlineData(0, "SQL,0\r")]
    [InlineData(19, "SQL,19\r")]
    public void BuildSquelchCommand_FramesSupportedLevels(int level, string expected)
    {
        Assert.Equal(expected, ScannerCommands.BuildSquelchCommand(level));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(20)]
    public void BuildSquelchCommand_RejectsUnsupportedLevels(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerCommands.BuildSquelchCommand(level));
    }

    [Theory]
    [InlineData("VOL,0\r", 0)]
    [InlineData("VOL,29\r", 29)]
    public void TryParseVolumeLevel_ParsesSupportedLevels(string response, int expected)
    {
        Assert.True(ScannerCommands.TryParseVolumeLevel(response, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("SQL,0\r", 0)]
    [InlineData("SQL,19\r", 19)]
    public void TryParseSquelchLevel_ParsesSupportedLevels(string response, int expected)
    {
        Assert.True(ScannerCommands.TryParseSquelchLevel(response, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("VOL,30\r")]
    [InlineData("SQL,20\r")]
    [InlineData("VOL,invalid\r")]
    [InlineData("VER,1.2\r")]
    public void TryParseLevel_RejectsUnexpectedOrOutOfRangeResponses(string response)
    {
        Assert.False(ScannerCommands.TryParseVolumeLevel(response, out _));
        Assert.False(ScannerCommands.TryParseSquelchLevel(response, out _));
    }

    [Theory]
    [InlineData("VOL,30\r")]
    [InlineData("SQL,invalid\r")]
    public void TryParseLevel_ResetsOutValueWhenParsingFails(string response)
    {
        Assert.False(ScannerCommands.TryParseVolumeLevel(response, out var volume));
        Assert.Equal(0, volume);
        Assert.False(ScannerCommands.TryParseSquelchLevel(response, out var squelch));
        Assert.Equal(0, squelch);
    }

    [Fact]
    public void ScannerInfoParser_ReadsKnownStatusValues()
    {
        const string xml = "<ScannerInfo><Mode>SCAN_MODE</Mode><Frequency>154.7</Frequency><System>County</System><Department>Fire</Department><Site>North</Site><Channel>Dispatch</Channel><DB_Counter>42</DB_Counter></ScannerInfo>\r";

        var status = ScannerInfoParser.Parse(xml);

        Assert.Equal("SCAN_MODE", status.Mode);
        Assert.Equal("154.7", status.Frequency);
        Assert.Equal("County", status.System);
        Assert.Equal("Fire", status.Department);
        Assert.Equal("North", status.Site);
        Assert.Equal("Dispatch", status.Channel);
        Assert.Equal(42, status.DatabaseCounter);
    }

    [Fact]
    public void ScannerInfoParser_RejectsEmptyXml()
    {
        Assert.Throws<ArgumentException>(() => ScannerInfoParser.Parse(""));
    }

    [Theory]
    [InlineData("<ScannerInfo>")]
    [InlineData("not xml")]
    public void ScannerInfoParser_RejectsMalformedXml(string xml)
    {
        Assert.Throws<System.Xml.XmlException>(() => ScannerInfoParser.Parse(xml));
    }

    [Theory]
    [InlineData("<GLT><FavoritesList /></GLT>")]
    [InlineData("<Other><Mode>SCAN_MODE</Mode></Other>")]
    public void ScannerInfoParser_RejectsUnexpectedXmlRoot(string xml)
    {
        Assert.Throws<System.Xml.XmlException>(() => ScannerInfoParser.Parse(xml));
    }

    [Fact]
    public void ScannerInfoParser_LeavesMissingOptionalValuesUnset()
    {
        var status = ScannerInfoParser.Parse("<ScannerInfo><Mode>SCAN_MODE</Mode></ScannerInfo>\r");

        Assert.Equal("SCAN_MODE", status.Mode);
        Assert.Null(status.Frequency);
        Assert.Null(status.System);
        Assert.Null(status.DatabaseCounter);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("2147483648")]
    public void ScannerInfoParser_RejectsMalformedDatabaseCounter(string counter)
    {
        var xml = $"<ScannerInfo><DB_Counter>{counter}</DB_Counter></ScannerInfo>";

        Assert.Throws<System.Xml.XmlException>(() => ScannerInfoParser.Parse(xml));
    }
}
