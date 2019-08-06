using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Downloader.Gidora;
using log4net;
using log4net.Config;

namespace Downloader
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
                Console.WriteLine("Usage <url> [url2] [url3]... <number_of_threads>");
                return;
            }

            log.Info($"FileUrl = {args[0]}, Threads = {args[1]}");

            var mutexes = new WaitHandle[args.Length -1];
            var downloads = new Dictionary<string, ManualResetEvent>();

            var downloader = new GidoraDownloader();
            downloader.DownloadCompleted += (sender, eventArgs) =>
            {
                var result = eventArgs.Result;

                log.Info($"Completed. Path = {result.FilePath} Length = {result.BytesDownloaded}");

                if (!result.FileExists)
                {
                    log.Info($"File {args[0]} does not exist");
                }
                else
                {
                    log.Info($"Location: {result.FilePath}");
                    log.Info($"Size: {result.BytesDownloaded}bytes");
                    log.Info($"Time taken: {result.TimeTakenMs}ms");
                    log.Info($"Parallel: {result.ParallelDownloads}");
                    //Console.WriteLine($"Total time: {sw.ElapsedMilliseconds}ms");
                }

                downloads[result.FileUrl].Set();
            };
            double lastPercent = 0.0;
            var lastPercents = new Dictionary<string, double>();
            downloader.ProgressChanged += (sender, eventArgs) =>
            {
                lock (lastPercents)
                {
                    lastPercent = lastPercents[eventArgs.FileUrl];
                }

                double percent = (double) eventArgs.Progress / eventArgs.FileLength * 100.0;

                if (percent >= lastPercent + 1.0 || eventArgs.Progress == eventArgs.FileLength)
                {
                    log.Info($"Path = {eventArgs.FileUrl} Progress = {percent:###}%");
                    lastPercent = percent;
                    lock (lastPercents)
                    {
                        lastPercents[eventArgs.FileUrl] = lastPercent;
                    }
                }
            };

            downloader.BandwidthMeasured += (sender, eventArgs) =>
            {
                log.Info($"Path = {eventArgs.Bandwidth.FileUrl} 1sec = {eventArgs.Bandwidth.Mean1Second} 5sec = {eventArgs.Bandwidth.Mean5Seconds} 30sec = {eventArgs.Bandwidth.Mean30Seconds} 1min = {eventArgs.Bandwidth.Mean1Minute} Remaining = {eventArgs.Bandwidth.Remaining}" );
            };

            for (int i = 0; i < args.Length - 1; i++)
            {
                mutexes[i] = new ManualResetEvent(false);
                downloads.Add(args[i], (ManualResetEvent)mutexes[i]);
                lastPercents.Add(args[i], 0.0);

                //Calculate destination path  
                string filePath = new Uri(args[i]).Segments.Last();

                downloader.DownloadAsync(args[i], filePath, int.Parse(args[args.Length - 1]));
            }

            WaitHandle.WaitAll(mutexes);
        }
    }
}