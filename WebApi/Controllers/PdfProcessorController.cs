using Core;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PdfProcessorController(IPdfExtractor pdfExtractor, ILogger<PdfProcessorController> logger, IOcrProcessor ocrProcessor) : ControllerBase
    {
        [HttpGet("convert")]
        public async Task<ActionResult> RunConvertAllPdfToImagesAsync([FromQuery] int nrOfDocs = 5)
        {
            string trace_guid = Guid.NewGuid().ToString();

            int count = 0;
            var files = Directory.GetFiles(IWorkPaths.inputPath);

            if (files.Length == 0)
                return BadRequest("empty input folder");

            foreach (var item in files)
            {
                if (count == nrOfDocs)
                    continue;

                try
                {
                    string result = await pdfExtractor.ConvertPdfToImagesAsync(Path.GetFileName(item), trace_guid);
                    Console.WriteLine(result);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    continue;
                }

                count++;
            }

            return Ok("Conversion completed.");
        }

        [HttpGet("ocr")]
        public async Task<ActionResult> RunOCR([FromQuery] string trace_guid = "bbb62b0d-b71a-4898-9776-0ac1dbfbf261", int nrOfItems = 5)
        {
            try
            {
                string result = await ocrProcessor.ProcessFolderAsync(trace_guid, nrOfItems);
                return Ok(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, ex.Message);
            }
        }
    }
}
