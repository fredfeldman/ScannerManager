namespace SDS200_ControlApp.Protocol;

public sealed record ScannerRecordingResponse(int? Status, string? ErrorCode, bool IsAcknowledgement);

public static class ScannerCommands
{
    public const int QuickKeyStatusCount = 100;
    public const int FavoritesListQuickKeyCount = QuickKeyStatusCount;

    public static string BuildStatusPush(int intervalMilliseconds)
    {
        if (intervalMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        }

        return Frame($"PSI,{intervalMilliseconds}");
    }

    public static string BuildVolumeCommand(int level)
    {
        if (level is < 0 or > 29)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "SDS200 volume must be between 0 and 29.");
        }

        return Frame($"VOL,{level}");
    }

    public static string BuildSquelchCommand(int level)
    {
        if (level is < 0 or > 19)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "SDS200 squelch must be between 0 and 19.");
        }

        return Frame($"SQL,{level}");
    }

    public static string BuildStartScanCommand()
    {
        return Frame("JPM,SCN_MODE,4294967295");
    }

    public static string BuildPowerOffCommand()
    {
        return Frame("POF");
    }

    public static string BuildGetRecordingStatusCommand()
    {
        return Frame("URC");
    }

    public static string BuildSetRecordingStatusCommand(bool isRecording)
    {
        return Frame($"URC,{(isRecording ? 1 : 0)}");
    }

    public static bool TryParseRecordingResponse(string response, out ScannerRecordingResponse? recordingResponse)
    {
        recordingResponse = null;
        var fields = Split(response);
        if (fields.Length == 2 && fields[0] == "URC" && int.TryParse(fields[1], out var status) && status is 0 or 1)
        {
            recordingResponse = new ScannerRecordingResponse(status, null, false);
            return true;
        }

        if (fields.Length == 2 && fields[0] == "URC" && fields[1] == "OK")
        {
            recordingResponse = new ScannerRecordingResponse(null, null, true);
            return true;
        }

        if (fields.Length == 3 && fields[0] == "URC" && fields[1] == "ERR" && !string.IsNullOrWhiteSpace(fields[2]))
        {
            recordingResponse = new ScannerRecordingResponse(null, fields[2], false);
            return true;
        }

        return false;
    }

    public static string BuildHoldCommand(string target, string firstTag, string secondTag)
    {
        return Frame($"HLD,{ValidateField(target, nameof(target))},{ValidateField(firstTag, nameof(firstTag))},{ValidateField(secondTag, nameof(secondTag))}");
    }

    public static string BuildNextCommand(string target, string firstTag, string secondTag, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return Frame($"NXT,{ValidateField(target, nameof(target))},{ValidateField(firstTag, nameof(firstTag))},{ValidateField(secondTag, nameof(secondTag))},{count}");
    }

    public static string BuildPreviousCommand(string target, string firstTag, string secondTag, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return Frame($"PRV,{ValidateField(target, nameof(target))},{ValidateField(firstTag, nameof(firstTag))},{ValidateField(secondTag, nameof(secondTag))},{count}");
    }

    public static string BuildJumpToTagCommand(string favoritesListTag, string systemTag, string channelTag)
    {
        return Frame($"JNT,{ValidateField(favoritesListTag, nameof(favoritesListTag))},{ValidateField(systemTag, nameof(systemTag))},{ValidateField(channelTag, nameof(channelTag))}");
    }

    public static string BuildSetAvoidCommand(string target, string firstTag, string secondTag, int status)
    {
        if (status is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Avoid status must be 1 (permanent), 2 (temporary), or 3 (remove avoid).");
        }

        return Frame($"AVD,{ValidateField(target, nameof(target))},{ValidateField(firstTag, nameof(firstTag))},{ValidateField(secondTag, nameof(secondTag))},{status}");
    }

    public static string BuildGetFavoritesListQuickKeysCommand()
    {
        return Frame("FQK");
    }

    public static string BuildGetFavoritesListsCommand()
    {
        return Frame("GLT,FL");
    }

    public static string BuildGetSystemsCommand(int favoritesListIndex)
    {
        return Frame($"GLT,SYS,{ValidateRuntimeIndex(favoritesListIndex, nameof(favoritesListIndex))}");
    }

    public static string BuildGetDepartmentsCommand(int systemIndex)
    {
        return Frame($"GLT,DEPT,{ValidateRuntimeIndex(systemIndex, nameof(systemIndex))}");
    }

    public static string BuildGetSitesCommand(int systemIndex)
    {
        return Frame($"GLT,SITE,{ValidateRuntimeIndex(systemIndex, nameof(systemIndex))}");
    }

    public static string BuildGetConventionalFrequenciesCommand(int departmentIndex)
    {
        return Frame($"GLT,CFREQ,{ValidateRuntimeIndex(departmentIndex, nameof(departmentIndex))}");
    }

    public static string BuildGetTalkgroupsCommand(int departmentIndex)
    {
        return Frame($"GLT,TGID,{ValidateRuntimeIndex(departmentIndex, nameof(departmentIndex))}");
    }

    public static string BuildGetSiteFrequenciesCommand(int siteIndex)
    {
        return Frame($"GLT,SFREQ,{ValidateRuntimeIndex(siteIndex, nameof(siteIndex))}");
    }

    public static string BuildSetFavoritesListQuickKeysCommand(IEnumerable<int> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var values = statuses.ToArray();
        if (values.Length != FavoritesListQuickKeyCount)
        {
            throw new ArgumentException($"Exactly {FavoritesListQuickKeyCount} Quick Key statuses are required.", nameof(statuses));
        }

        if (values.Any(status => status is < 0 or > 2))
        {
            throw new ArgumentOutOfRangeException(nameof(statuses), "Quick Key statuses must be 0, 1, or 2.");
        }

        return Frame($"FQK,{string.Join(',', values)}");
    }

    public static bool TryParseFavoritesListQuickKeys(string response, out int[] statuses)
    {
        statuses = [];
        return TryParseQuickKeyStatuses(response, "FQK", [], out statuses);
    }

    public static string BuildGetSystemQuickKeysCommand(int favoritesListQuickKey)
    {
        return Frame($"SQK,{ValidateQuickKeyIndex(favoritesListQuickKey, nameof(favoritesListQuickKey))}");
    }

    public static string BuildSetSystemQuickKeysCommand(int favoritesListQuickKey, IEnumerable<int> statuses)
    {
        var parentKeys = new[] { ValidateQuickKeyIndex(favoritesListQuickKey, nameof(favoritesListQuickKey)) };
        return BuildQuickKeySetCommand("SQK", parentKeys, statuses);
    }

    public static bool TryParseSystemQuickKeys(string response, int favoritesListQuickKey, out int[] statuses)
    {
        statuses = [];
        var parentKeys = new[] { ValidateQuickKeyIndex(favoritesListQuickKey, nameof(favoritesListQuickKey)) };
        return TryParseQuickKeyStatuses(response, "SQK", parentKeys, out statuses);
    }

    public static string BuildGetDepartmentQuickKeysCommand(int favoritesListQuickKey, int systemQuickKey)
    {
        return Frame($"DQK,{ValidateQuickKeyIndex(favoritesListQuickKey, nameof(favoritesListQuickKey))},{ValidateQuickKeyIndex(systemQuickKey, nameof(systemQuickKey))}");
    }

    public static string BuildSetDepartmentQuickKeysCommand(int favoritesListQuickKey, int systemQuickKey, IEnumerable<int> statuses)
    {
        var parentKeys = new[]
        {
            ValidateQuickKeyIndex(favoritesListQuickKey, nameof(favoritesListQuickKey)),
            ValidateQuickKeyIndex(systemQuickKey, nameof(systemQuickKey))
        };
        return BuildQuickKeySetCommand("DQK", parentKeys, statuses);
    }

    public static bool TryParseDepartmentQuickKeys(string response, int favoritesListQuickKey, int systemQuickKey, out int[] statuses)
    {
        statuses = [];
        var parentKeys = new[]
        {
            ValidateQuickKeyIndex(favoritesListQuickKey, nameof(favoritesListQuickKey)),
            ValidateQuickKeyIndex(systemQuickKey, nameof(systemQuickKey))
        };
        return TryParseQuickKeyStatuses(response, "DQK", parentKeys, out statuses);
    }

    public static bool TryParseVolumeLevel(string response, out int level)
    {
        return TryParseLevel(response, "VOL", 29, out level);
    }

    public static bool TryParseSquelchLevel(string response, out int level)
    {
        return TryParseLevel(response, "SQL", 19, out level);
    }

    public static string Frame(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var unframedCommand = command.EndsWith('\r') ? command[..^1] : command;
        if (unframedCommand.Contains('\r') || unframedCommand.Contains('\n'))
        {
            throw new ArgumentException("A command must contain exactly one frame and cannot contain embedded line breaks.", nameof(command));
        }

        if (unframedCommand.Any(character => character is < ' ' or > '~'))
        {
            throw new ArgumentException("Scanner commands must contain printable ASCII characters only.", nameof(command));
        }

        return $"{unframedCommand}\r";
    }

    public static bool TryParseModel(string response, out string? model)
    {
        var fields = Split(response);
        var parsedModel = fields.Length == 2 && fields[0] == "MDL" ? fields[1].Trim() : null;
        model = string.IsNullOrWhiteSpace(parsedModel) ? null : parsedModel;
        return model is not null;
    }

    public static bool TryParseFirmware(string response, out string? firmware)
    {
        var fields = Split(response);
        firmware = fields.Length >= 2 && fields[0] == "VER"
            ? string.Join(',', fields.Skip(1)).Trim()
            : null;
        return !string.IsNullOrWhiteSpace(firmware);
    }

    private static string[] Split(string response)
    {
        return response.TrimEnd('\r', '\n').Split(',', StringSplitOptions.None);
    }

    private static bool TryParseLevel(string response, string command, int maximum, out int level)
    {
        level = 0;
        var fields = Split(response);
        if (fields.Length != 2 || fields[0] != command ||
            !int.TryParse(fields[1], out var parsedLevel) || parsedLevel is < 0 || parsedLevel > maximum)
        {
            return false;
        }

        level = parsedLevel;
        return true;
    }

    private static string BuildQuickKeySetCommand(string command, int[] parentKeys, IEnumerable<int> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var values = statuses.ToArray();
        ValidateQuickKeyStatuses(values, nameof(statuses));
        var fields = parentKeys.Select(value => value.ToString()).Concat(values.Select(value => value.ToString()));
        return Frame($"{command},{string.Join(',', fields)}");
    }

    private static bool TryParseQuickKeyStatuses(string response, string command, int[] parentKeys, out int[] statuses)
    {
        statuses = [];
        var fields = Split(response);
        if (fields.Length != 1 + parentKeys.Length + QuickKeyStatusCount || fields[0] != command)
        {
            return false;
        }

        for (var index = 0; index < parentKeys.Length; index++)
        {
            if (!int.TryParse(fields[index + 1], out var parentKey) || parentKey != parentKeys[index])
            {
                return false;
            }
        }

        var parsedStatuses = new int[QuickKeyStatusCount];
        for (var index = 0; index < parsedStatuses.Length; index++)
        {
            if (!int.TryParse(fields[index + parentKeys.Length + 1], out var status) || status is < 0 or > 2)
            {
                return false;
            }

            parsedStatuses[index] = status;
        }

        statuses = parsedStatuses;
        return true;
    }

    private static void ValidateQuickKeyStatuses(int[] statuses, string parameterName)
    {
        if (statuses.Length != QuickKeyStatusCount)
        {
            throw new ArgumentException($"Exactly {QuickKeyStatusCount} Quick Key statuses are required.", parameterName);
        }

        if (statuses.Any(status => status is < 0 or > 2))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Quick Key statuses must be 0, 1, or 2.");
        }
    }

    private static int ValidateQuickKeyIndex(int index, string parameterName)
    {
        if (index is < 0 or >= QuickKeyStatusCount)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Quick Key index must be between 0 and 99.");
        }

        return index;
    }

    private static int ValidateRuntimeIndex(int index, string parameterName)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "GLT runtime indexes cannot be negative.");
        }

        return index;
    }

    private static string ValidateField(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("A command field cannot contain a comma or line terminator.", parameterName);
        }

        return value;
    }
}
