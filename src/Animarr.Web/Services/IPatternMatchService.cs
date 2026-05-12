using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

public interface IPatternMatchService
{
    FileKind DetermineFileKind(string extension);
    bool IsIgnored(string fileName, IEnumerable<IgnoreRule> rules);
    ParseResult ParseFileName(string fileName, IEnumerable<RenamePattern> patterns);
    int? DetectSeasonFromPath(string folderPath, string? rootPath = null, int maxDepth = 5);
    string? BuildTargetName(ParseResult parse, int? seasonFromPath, FileKind kind, string extension,
        string? episodeName = null);
    RenamePreviewItem EvaluateFile(
        string filePath,
        IEnumerable<RenamePattern> patterns,
        IEnumerable<IgnoreRule> ignoreRules,
        FolderType folderType = FolderType.Auto,
        bool isSection = false,
        string? folderRoot = null,
        IReadOnlyDictionary<(int s, int ep), string>? episodeNames = null);
}
