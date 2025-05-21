
namespace Core
{
    public interface IPdfExtractor
    {
        Task<string> ConvertPdfToImagesAsync(string fileName, string trace_guid);
    }
}
