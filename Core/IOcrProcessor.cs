namespace Core
{
    public interface IOcrProcessor
    {
        Task<string> ProcessFolderAsync(string traceDocumentFolder, int nrOfEntries = 5);
    }
}