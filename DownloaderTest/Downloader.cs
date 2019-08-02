using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace Downloader.App
{
    public class DownloadCompletedEventArgs : EventArgs
    {
        public string FileUrl { get; set; }
        public string FilePath { get; set; }
        public long FileLength { get; set; }
    }

    public class Downloader
    {
        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;

        public Downloader()
        {
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 100;
            ServicePointManager.MaxServicePointIdleTime = 1000;
        }

        public void OnDownloadComplete(string fileUrl, string filePath, long fileLength)
        {
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs { FileUrl = fileUrl, FilePath = filePath, FileLength = fileLength});
        }

        public DownloadResult Download(string fileUrl, string destinationFolderPath, int numberOfParallelDownloads = 0, bool validateSSL = false)
        {
            if (!validateSSL)
            {
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            }

            Uri uri = new Uri(fileUrl);

            //Calculate destination path  
            string filePath = Path.Combine(destinationFolderPath, uri.Segments.Last());

            var result = new DownloadResult { FilePath = filePath };

            //Handle number of parallel downloads  
            if (numberOfParallelDownloads <= 0)
            {
                numberOfParallelDownloads = Environment.ProcessorCount;
            }

            //Get file size  
            WebRequest webRequest = HttpWebRequest.Create(fileUrl);
            webRequest.Method = "HEAD";
            long responseLength;
            using (WebResponse webResponse = webRequest.GetResponse())
            {
                responseLength = long.Parse(webResponse.Headers.Get("Content-Length"));
                result.Size = responseLength;
            }

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var readRanges = new List<Range>();
            long lastRangeStart = 0;
            for (int chunk = 0; chunk < numberOfParallelDownloads - 1; chunk++)
            {
                var range = new Range()
                {
                    Start = chunk * (responseLength / numberOfParallelDownloads),
                    End = ((chunk + 1) * (responseLength / numberOfParallelDownloads)) - 1
                };
                readRanges.Add(range);
                lastRangeStart = range.End + 1;
            }

            readRanges.Add(new Range
            {
                Start = lastRangeStart,
                End = responseLength - 1
            });

            for (int i = 0; i < readRanges.Count; i++)
            {
                readRanges[i].Index = i;
                readRanges[i].Buffer = new byte[readRanges[i].End - readRanges[i].Start + 1];
                readRanges[i].Mutex = new ManualResetEvent(false);
            }

            var sw = Stopwatch.StartNew();
            long bytesDownloaded = DownloadRanges(fileUrl, readRanges);
            sw.Stop();

            result.TimeTakenMs = sw.ElapsedMilliseconds;
            result.ParallelDownloads = readRanges.Count;

            //Merge to single file  
            using (var destinationStream = new FileStream(filePath, FileMode.Append))
            {
                for (int i = 0; i < readRanges.Count; i++)
                {
                    destinationStream.Write(readRanges[i].Buffer, 0, readRanges[i].Buffer.Length);
                }
                destinationStream.Flush();
            }

            OnDownloadComplete(fileUrl, filePath, bytesDownloaded);

            return result;
        }

        private long DownloadRanges(string fileUrl, List<Range> readRanges)
        { 
            // Parallel download  
            long bytesDownloaded = 0;
            var mutexes = new WaitHandle[readRanges.Count];
            for (int i = 0; i < readRanges.Count; i++)
                mutexes[i] = readRanges[i].Mutex;

            foreach (var readRange in readRanges)
            {
                new Thread(_ =>
                {
                    Console.WriteLine($"{readRange.Index} started. Range: {readRange.Start}-{readRange.End}");

                    int rangeLen = readRange.Buffer.Length;
                    int offset = 0;
                    const int blockSize = 4096;
                    int triesCount = 0;
                    int maxTriesCount = 100;
                    bool success = false;

                    while (!success && triesCount < maxTriesCount)
                    {
                        try
                        {
                            HttpWebRequest httpWebRequest = WebRequest.Create(fileUrl) as HttpWebRequest;
                            httpWebRequest.Method = "GET";
                            httpWebRequest.AddRange((int)readRange.Start + offset, (int)readRange.End);
                            httpWebRequest.Timeout = 30000;
                            httpWebRequest.ReadWriteTimeout = 30000;
                            using (HttpWebResponse httpWebResponse = httpWebRequest.GetResponse() as HttpWebResponse)
                            {
                                if (httpWebResponse.StatusCode != HttpStatusCode.PartialContent)
                                    throw new Exception("Server does not support accept:range");

                                using (var responseStream = httpWebResponse.GetResponseStream())
                                {
                                    int bytesRead;
                                    while ((bytesRead = responseStream.Read(readRange.Buffer,
                                               offset,
                                               rangeLen - offset < blockSize ? rangeLen - offset : blockSize
                                           )) > 0)
                                    {
                                        offset += bytesRead;
                                        Interlocked.Add(ref bytesDownloaded, bytesRead);
                                    }
                                }

                                success = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Exception {ex.Message} index={readRange.Index} offset={offset}");
                            triesCount++;
                            Thread.Sleep(10000);
                        }
                    }

                    readRange.Mutex.Set();
                    Console.WriteLine($"{readRange.Index} completed. Range: {readRange.Start}-{readRange.End}");

                }){ IsBackground = true}.Start();
            }

            WaitHandle.WaitAll(mutexes);

            return bytesDownloaded;
        }
    }
}