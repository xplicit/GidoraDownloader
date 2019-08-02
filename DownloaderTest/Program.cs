using System;
using System.Diagnostics;
using System.Net;

namespace Downloader.App
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage <url> <number_of_threads>");
                return;
            }

            ServicePointManager.DefaultConnectionLimit = 100;

            Console.WriteLine($"Connection limit: {ServicePointManager.DefaultConnectionLimit}");

            var sw = Stopwatch.StartNew();

            var downloader = new Downloader();
            downloader.DownloadCompleted += (sender, eventArgs) =>
            {
                Console.WriteLine($"Completed. Path = {eventArgs.FilePath} Length = {eventArgs.FileLength}");
            };

            var result = downloader.Download(args[0], @".", int.Parse(args[1]));

            sw.Stop();

            Console.WriteLine($"Location: {result.FilePath}");
            Console.WriteLine($"Size: {result.Size}bytes");
            Console.WriteLine($"Time taken: {result.TimeTakenMs}ms");
            Console.WriteLine($"Parallel: {result.ParallelDownloads}");
            Console.WriteLine($"Total time: {sw.ElapsedMilliseconds}ms");
        }
    }
}