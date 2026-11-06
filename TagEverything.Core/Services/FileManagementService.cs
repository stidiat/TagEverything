namespace TagEverything.Core.Services;

public class FileManagementService(string libraryRootPath)
{
    private string _libraryRootPath = libraryRootPath;

    public void UpdateLibraryRoot(string libraryRootPath)
    {
        _libraryRootPath = libraryRootPath;
    }

    public string GetLibraryFilePath(string relativePath)
    {
        return LibraryPaths.Resolve(_libraryRootPath, relativePath, contentOnly: true);
    }
}
