using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using DownloaderTest;
using log4net;

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
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public int MaxTriesCount { get; set; } = 100;

        public int NetErrorWaitMs { get; set; } = 10000;

        public int TimeoutMs { get; set; } = 30000;

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

        public DownloadFileInfo GetFileInfo(string fileUrl)
        {
            var info = new DownloadFileInfo { FileUrl = fileUrl};

            bool success = false;
            int triesCount = 0;

            while (!success && triesCount < MaxTriesCount)
            {
                try
                {
                    //we must create web request each time to prevent DNS caching
                    var webRequest = (HttpWebRequest)WebRequest.Create(fileUrl);
                    webRequest.Method = "HEAD";
                    webRequest.Timeout = TimeoutMs;
                    webRequest.ReadWriteTimeout = TimeoutMs;

                    using (var webResponse = webRequest.GetResponse())
                    {
                        info.Length = long.Parse(webResponse.Headers.Get("Content-Length"));
                        info.IsSupportedHead = true;
                        info.Exists = true;
                        success = true;
                    }
                }
                catch (WebException ex)
                {
                    var status = (ex.Response as HttpWebResponse)?.StatusCode;

                    //File does not exist on server, return
                    if (status == HttpStatusCode.Forbidden || status == HttpStatusCode.NotFound)
                        return info;

                    log.Warn("WebException during getting HEAD", ex);
                    WaitNetwork(ref triesCount);
                }
                catch (Exception ex)
                {
                    log.Warn("Exception during getting HEAD download", ex);
                    WaitNetwork(ref triesCount);
                }
            }

            success = false;
            triesCount = 0;
            while (!success && triesCount < MaxTriesCount)
            {
                try
                {
                    //we must create web request each time to prevent DNS caching
                    var webRequest = (HttpWebRequest)WebRequest.Create(fileUrl);
                    webRequest.AddRange(0, (int)info.Length - 1);
                    webRequest.Method = "HEAD";
                    webRequest.Timeout = TimeoutMs;
                    webRequest.ReadWriteTimeout = TimeoutMs;

                    using (var webResponse = (HttpWebResponse) webRequest.GetResponse())
                    {
                        info.IsSupportedHead = webResponse.StatusCode == HttpStatusCode.PartialContent;
                        success = true;
                    }
                }
                catch (WebException ex)
                {
                    var status = (ex.Response as HttpWebResponse)?.StatusCode;

                    //File does not exist on server, return
                    if (status == HttpStatusCode.Forbidden || status == HttpStatusCode.NotFound)
                        return info;

                    log.Warn("WebException during getting HEAD", ex);
                    WaitNetwork(ref triesCount);
                }
                catch (Exception ex)
                {
                    log.Warn("Exception during getting HEAD download", ex);
                    WaitNetwork(ref triesCount);
                }
            }
          

            return info;
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

            var downloadFileInfo = GetFileInfo(fileUrl);

            log.Info($"HEAD supported: {downloadFileInfo.IsSupportedHead}, Range supported: {downloadFileInfo.IsSupportedRange}");
            log.Info($"File {fileUrl} exists: {downloadFileInfo.Exists}, length = {downloadFileInfo.Length}");
            log.Info($"{downloadFileInfo.Length}");

            var readRanges = PrepareRanges(downloadFileInfo, numberOfParallelDownloads);

            var sw = Stopwatch.StartNew();
            long bytesDownloaded = DownloadRanges(fileUrl, readRanges, downloadFileInfo.IsSupportedRange);
            sw.Stop();

            result.TimeTakenMs = sw.ElapsedMilliseconds;
            result.ParallelDownloads = readRanges.Count;
            result.Size = bytesDownloaded;

            WriteFile(filePath, readRanges);

            OnDownloadComplete(fileUrl, filePath, bytesDownloaded);

            return result;
        }

        private void WriteFile(string filePath, List<Range> readRanges)
        {
            using (var destinationStream = new FileStream(filePath, FileMode.Create))
            {
                for (int i = 0; i < readRanges.Count; i++)
                {
                    destinationStream.Write(readRanges[i].Buffer, 0, readRanges[i].Buffer.Length);
                }
                destinationStream.Flush();
            }
        }

        private List<Range> PrepareRanges(DownloadFileInfo info, int numberOfParallelDownloads)
        {
            var readRanges = new List<Range>();

            long lastRangeStart = 0;

            if (info.IsSupportedRange)
            {
                for (int chunk = 0; chunk < numberOfParallelDownloads - 1; chunk++)
                {
                    var range = new Range
                    {
                        Start = chunk * (info.Length / numberOfParallelDownloads),
                        End = ((chunk + 1) * (info.Length / numberOfParallelDownloads)) - 1
                    };
                    readRanges.Add(range);
                    lastRangeStart = range.End + 1;
                }
            }

            //last range which we add always even if the Range header is not supported
            readRanges.Add(new Range
            {
                Start = lastRangeStart,
                End = info.Length - 1
            });

            for (int i = 0; i < readRanges.Count; i++)
            {
                readRanges[i].Index = i;
                readRanges[i].Buffer = new byte[readRanges[i].End - readRanges[i].Start + 1];
                readRanges[i].Mutex = new ManualResetEvent(false);
            }

            return readRanges;
        }

        private long DownloadRanges(string fileUrl, List<Range> readRanges, bool isSupportedRange)
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
                    log.Debug($"{readRange.Index} started. Range: {readRange.Start}-{readRange.End}");

                    int rangeLen = readRange.Buffer.Length;
                    int offset = 0;
                    const int blockSize = 4096;
                    int triesCount = 0;
                    bool success = false;

                    while (!success && triesCount < MaxTriesCount)
                    {
                        try
                        {
                            var httpWebRequest = (HttpWebRequest)WebRequest.Create(fileUrl);
                            httpWebRequest.Method = "GET";
                            if (isSupportedRange)
                                httpWebRequest.AddRange((int)readRange.Start + offset, (int)readRange.End);
                            httpWebRequest.Timeout = TimeoutMs;
                            httpWebRequest.ReadWriteTimeout = TimeoutMs;
                            using (var httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse())
                            {
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
                            //reset offset if server does not support range
                            if (!isSupportedRange)
                                offset = 0;

                            log.Warn($"Exception {ex.Message} index={readRange.Index} offset={offset}");
                            WaitNetwork(ref triesCount);
                        }
                    }

                    readRange.Mutex.Set();
                    log.Debug($"{readRange.Index} completed. Range: {readRange.Start}-{readRange.End}");

                }){ IsBackground = true}.Start();
            }

            WaitHandle.WaitAll(mutexes);

            return bytesDownloaded;
        }

        private void WaitNetwork(ref int triesCount)
        {
            triesCount++;
            Thread.Sleep(NetErrorWaitMs);
        }
    }
}