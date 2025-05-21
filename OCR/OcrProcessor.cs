using Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tesseract;

namespace OCR
{
    public class OcrProcessor : IWorkPaths, IOcrProcessor
    {
        private readonly SemaphoreSlim _reportLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentBag<OcrMetrics> _allMetrics = new ConcurrentBag<OcrMetrics>();
        private const int MaxParallelTasks = 100;

        public async Task<string> ProcessFolderAsync(string traceDocumentFolder, int nrOfEntries = 5)
        {
            // Construim calea completă către directorul de imagini
            string inputFolder = Path.Combine(IWorkPaths.output_img, traceDocumentFolder);
            string outputCsv = Path.Combine(IWorkPaths.reportPath, $"{traceDocumentFolder}_metrics.csv");

            // Verificăm dacă directorul există
            if (!Directory.Exists(inputFolder))
            {
                Console.WriteLine($"Directorul {inputFolder} nu există.");
                return null;
            }

            // Obținem toate directoarele din folder
            var imgDirectories = Directory.GetDirectories(inputFolder);

            // Limitează numărul de directoare procesate
            var directoriesToProcess = imgDirectories.Take(nrOfEntries).ToList();
            int totalDirs = directoriesToProcess.Count;

            if (totalDirs == 0)
            {
                Console.WriteLine($"Nu există directoare în {inputFolder}.");
                return null;
            }

            // Procesăm toate folderele în paralel cu maxim 10 task-uri concurente
            var progress = new ConcurrentDictionary<string, bool>();
            var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelTasks };

            Console.WriteLine($"Starting parallel processing of {totalDirs} directories with max {MaxParallelTasks} threads...");

            await Parallel.ForEachAsync(directoriesToProcess, options, async (dirPath, token) =>
            {
                await ProcessDirectoryAsync(dirPath, progress);
            });

            // Verificăm dacă am reușit să procesăm măcar un fișier
            if (_allMetrics.IsEmpty)
            {
                Console.WriteLine("Nu s-a putut procesa niciun fișier JPEG.");
                return null;
            }

            // Generăm raportul final
            await GenerateCsvReportAsync(outputCsv, _allMetrics.ToList());
            Console.WriteLine($"Metrics saved to: {outputCsv}");

            return outputCsv;
        }

        private async Task ProcessDirectoryAsync(string dirPath, ConcurrentDictionary<string, bool> progress)
        {
            var stopwatch = new Stopwatch();
            var dirName = new DirectoryInfo(dirPath).Name;
            var imgs = Directory.GetFiles(dirPath, "*.jpeg", SearchOption.AllDirectories);

            if (imgs.Length == 0)
            {
                Console.WriteLine($"Directorul {dirPath} este gol sau nu conține fișiere JPEG.");
                progress[dirPath] = true;
                return;
            }

            foreach (var imgFile in imgs)
            {
                string directoryPath = Path.GetDirectoryName(imgFile);
                string documentName = new DirectoryInfo(directoryPath).Name;
                string fileName = Path.GetFileNameWithoutExtension(imgFile);

                try
                {
                    stopwatch.Restart();

                    var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                    path = Path.Combine(path, "tessdata");
                    path = path.Replace("file:\\", "");

                    using var engine = new TesseractEngine(path, "eng", EngineMode.Default);
                    using var img = Pix.LoadFromFile(imgFile);
                    using var page = engine.Process(img);

                    var text = page.GetText();

                    await WriteTextAsync(text, documentName, fileName);

                    var words = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var wordCount = words.Length;

                    // Extract word confidences
                    var confidences = new List<float>();
                    using var iterator = page.GetIterator();
                    iterator.Begin();
                    do
                    {
                        if (iterator.IsAtBeginningOf(PageIteratorLevel.Word))
                        {
                            var confidence = iterator.GetConfidence(PageIteratorLevel.Word);
                            confidences.Add(confidence);
                        }
                    } while (iterator.Next(PageIteratorLevel.Word));

                    double processingTime = stopwatch.Elapsed.TotalSeconds;
                    stopwatch.Stop();

                    var metrics = CalculateMetrics(documentName, fileName, wordCount, confidences, processingTime);
                    _allMetrics.Add(metrics);

                    Console.WriteLine($"Procesat: {documentName}/{fileName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Eroare la procesarea fișierului {imgFile}: {ex.Message}");
                }
            }

            progress[dirPath] = true;
            Console.WriteLine($"Finished processing directory: {dirName}. Progress: {progress.Count(p => p.Value)}/{progress.Count}");
        }

        private async Task WriteTextAsync(string text, string? directoryPath, string fileName)
        {
            string path = Path.Combine(IWorkPaths.output_text, directoryPath);

            // Thread-safe directory creation
            if (!Directory.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(path);
                }
                catch (IOException)
                {
                    // Directory might have been created by another thread
                    if (!Directory.Exists(path))
                        throw;
                }
            }

            string filePath = Path.Combine(path, fileName + ".txt");

            // Use FileMode.Create to ensure we're not appending to existing files
            using FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using StreamWriter writer = new StreamWriter(fs, Encoding.UTF8);
            await writer.WriteAsync(text);
        }

        private OcrMetrics CalculateMetrics(string documentName, string imgName, int totalWords, List<float> confidences, double totalTime)
        {
            // Dacă nu avem confidențe, returnăm o metrică simplificată
            if (confidences.Count == 0)
            {
                return new OcrMetrics
                {
                    DocumentName = documentName,
                    ImgName = imgName,
                    TotalWords = totalWords,
                    TotalPages = 1,
                    MeanConfidence = 0,
                    MinConfidence = 0,
                    MaxConfidence = 0,
                    MedianConfidence = 0,
                    Percentile90 = 0,
                    ProcessingTime = totalTime,
                    ConfidenceSpread = "Nu există date de încredere"
                };
            }

            var sortedConfidences = confidences.OrderBy(c => c).ToList();
            int count = sortedConfidences.Count;

            // Calculăm mediana corect luând în considerare cazul când avem număr par de elemente
            double median;
            if (count % 2 == 0 && count > 0)
            {
                // Pentru număr par de elemente, luăm media celor două din mijloc
                median = (sortedConfidences[count / 2 - 1] + sortedConfidences[count / 2]) / 2.0;
            }
            else
            {
                // Pentru număr impar, luăm elementul din mijloc
                median = count > 0 ? sortedConfidences[count / 2] : 0;
            }

            (var under50Count, var under50Percent, var between50And70Count, var between50And70Percent,
            var between70And90Count, var between70And90Percent, var above90Count, var above90Percent) =
                CalculateConfidenceSpread(sortedConfidences);

            var metrics = new OcrMetrics
            {
                DocumentName = documentName,
                ImgName = imgName,
                TotalWords = totalWords,
                TotalPages = 1,
                MeanConfidence = sortedConfidences.Average(),
                MinConfidence = count > 0 ? sortedConfidences.First() : 0,
                MaxConfidence = count > 0 ? sortedConfidences.Last() : 0,
                MedianConfidence = median,
                Percentile90 = count > 0 ? sortedConfidences[(int)(count * 0.9)] : 0,
                ProcessingTime = totalTime,
                Under50Count = under50Count,
                Under50Percent = under50Percent,
                Between50And70Count = between50And70Count,
                Between50And70Percent = between50And70Percent,
                Between70And90Count = between70And90Count,
                Between70And90Percent = between70And90Percent,
                Above90Count = above90Count,
                Above90Percent = above90Percent
            };

            return metrics;
        }

        private (int, double, int, double, int, double, int, double) CalculateConfidenceSpread(List<float> confidences)
        {
            if (confidences.Count == 0)
                return (0, 0, 0, 0, 0, 0, 0, 0);

            int total = confidences.Count;
            int under50 = confidences.Count(c => c < 50);
            int between50And70 = confidences.Count(c => c >= 50 && c < 70);
            int between70And90 = confidences.Count(c => c >= 70 && c < 90);
            int above90 = confidences.Count(c => c >= 90);

            double percentUnder50 = (double)under50 / total * 100;
            double percentBetween50And70 = (double)between50And70 / total * 100;
            double percentBetween70And90 = (double)between70And90 / total * 100;
            double percentAbove90 = (double)above90 / total * 100;

            return (under50, percentUnder50, between50And70, percentBetween50And70, between70And90, percentBetween70And90, above90, percentAbove90);
        }

        private async Task GenerateCsvReportAsync(string outputCsv, List<OcrMetrics> metrics)
        {
            try
            {
                await _reportLock.WaitAsync();
                try
                {
                    // Asigură-te că directorul de ieșire există
                    string outputDir = Path.GetDirectoryName(outputCsv);
                    if (!Directory.Exists(outputDir))
                    {
                        Directory.CreateDirectory(outputDir);
                    }

                    bool fileExists = File.Exists(outputCsv);

                    using (FileStream fs = new FileStream(outputCsv, FileMode.Append, FileAccess.Write, FileShare.None))
                    using (StreamWriter writer = new StreamWriter(fs, Encoding.UTF8))
                    {
                        // Write header only if the file doesn't exist
                        if (!fileExists)
                        {
                            await writer.WriteLineAsync("Document Name; ImgName; Total Pages; Total Words; Mean Confidence; Min Confidence; Max Confidence; Median Confidence; 90th Percentile; Processing Time (s); <50 Count; <50 Percent; 50-70 Count; 50-70 Percent; 70-90 Count; 70-90 Percent; 90-100 Count; 90-100 Percent");
                        }

                        // Write the metrics
                        foreach (var metric in metrics)
                        {
                            await writer.WriteLineAsync($"{metric.DocumentName}; {metric.ImgName}; {metric.TotalPages}; {metric.TotalWords}; {metric.MeanConfidence:F2}; {metric.MinConfidence:F2}; {metric.MaxConfidence:F2}; {metric.MedianConfidence:F2}; {metric.Percentile90:F2}; {metric.ProcessingTime:F2}; {metric.Under50Count}; {metric.Under50Percent:F1}; {metric.Between50And70Count}; {metric.Between50And70Percent:F1}; {metric.Between70And90Count}; {metric.Between70And90Percent:F1}; {metric.Above90Count}; {metric.Above90Percent:F1}");
                        }
                    }
                }
                finally
                {
                    _reportLock.Release();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Eroare la generarea raportului CSV: {ex.Message}");
            }
        }
    }

    public class OcrMetrics
    {
        public string DocumentName { get; set; }
        public string ImgName { get; set; }
        public int TotalPages { get; set; }
        public int TotalWords { get; set; }
        public double MeanConfidence { get; set; }
        public double MinConfidence { get; set; }
        public double MaxConfidence { get; set; }
        public double MedianConfidence { get; set; }
        public double Percentile90 { get; set; }
        public double ProcessingTime { get; set; }
        public string ConfidenceSpread { get; set; }
        public int Under50Count { get; set; }
        public double Under50Percent { get; set; }
        public int Between50And70Count { get; set; }
        public double Between50And70Percent { get; set; }
        public int Between70And90Count { get; set; }
        public double Between70And90Percent { get; set; }
        public int Above90Count { get; set; }
        public double Above90Percent { get; set; }
    }
}