using System;
using System.Diagnostics;
using System.Net;
using System.Security.Policy;
using log4net;
using log4net.Config;

namespace Downloader.App
{
    class Program
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        static void Main(string[] args)
        {
            XmlConfigurator.Configure();
            log.Info("Downloader tool started");

            if (args.Length < 2)
            {
                Console.WriteLine("Usage <url> <number_of_threads>");
                return;
            }

            log.Info($"FileUrl = {args[0]}, Threads = {args[1]}");

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