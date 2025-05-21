using Core;
using PDFtoImage;

namespace PdfProcessor
{
    public class PdfExtractor : IPdfExtractor, IWorkPaths
    {
        public async Task<string> ConvertPdfToImagesAsync(string fileName, string trace_guid)
        {
            string outputFolder = $"{trace_guid}_{fileName.Split(".")[0]}";
            Directory.CreateDirectory(GetFullOutputFolder(outputFolder));

            // Citește PDF-ul ca byte[]
            string filePath = Path.Join(IWorkPaths.inputPath, fileName);
            byte[] pdfBytes = await File.ReadAllBytesAsync(filePath);

            // Creează un MemoryStream pentru fiecare pagină
            int pageCount = Conversion.GetPageCount(new MemoryStream(pdfBytes));
            for (int i = 0; i < pageCount; i++)
            {
                using var pageStream = new MemoryStream(pdfBytes);
                string outputPath = Path.Combine(GetFullOutputFolder(outputFolder), $"page_{i + 1}.jpeg");
                Conversion.SaveJpeg(outputPath, pageStream, i, leaveOpen: false);
                Console.WriteLine($"Saved: {outputPath}");
            }

            Console.WriteLine("Conversion complete!");
            return "Conversion complete!";
        }

        private string GetFullOutputFolder(string outputFolder)
        {
            return Path.Join(IWorkPaths.output_img, outputFolder);
        }
    }
}
