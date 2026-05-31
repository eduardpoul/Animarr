using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

public interface IPatternMatchService
{
    FileKind DetermineFileKind(string extension);
    ParseResult ParseFileName(string fileName, IEnumerable<RenamePattern> patterns);
    int? DetectSeasonFromPath(string folderPath, string? rootPath = null, int maxDepth = 5);
}
