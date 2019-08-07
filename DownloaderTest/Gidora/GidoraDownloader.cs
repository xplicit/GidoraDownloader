using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using log4net;

namespace Downloader.Gidora
{
    public class BandwidthEventArgs : EventArgs
    {
        public Bandwidth Bandwidth { get; set; }
    }

    public class DownloadCompletedEventArgs : EventArgs
    {
        public DownloadResult Result { get; set; }
    }

    public class ProgressChangedEventArgs : EventArgs
    {
        public string FileUrl { get; set; }
        public long Progress { get; set; }
        public long FileLength { get; set; }

        public ProgressChangedEventArgs ShallowCopy() => (ProgressChangedEventArgs)MemberwiseClone();
    }

    public class BatchBandwidthEventArgs : EventArgs
    {
    }

    public class BatchDownloadCompletedEventArgs : EventArgs
    {
    }

    public class BatchProgressChangedEventArgs : EventArgs
    {
    }


    public class GidoraDownloader : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public int MaxTriesCount { get; set; } = 100;

        public int NetErrorWaitMs { get; set; } = 10000;

        public int TimeoutMs { get; set; } = 30000;

        public event EventHandler<BandwidthEventArgs> BandwidthMeasured;

        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;

        public event EventHandler<ProgressChangedEventArgs> ProgressChanged;

        public event EventHandler<BatchProgressChangedEventArgs> BatchProgressChanged;

        public event EventHandler<BatchBandwidthEventArgs> BatchBandwidthMeasured;

        public event EventHandler<BatchDownloadCompletedEventArgs> BatchDownloadCompleted;

        public GidoraDownloader()
        {
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 100;
            ServicePointManager.MaxServicePointIdleTime = 1000;
        }

        private volatile bool stopProgressThread;
        private static int batchId;

        private void StartProgressThread(ProgressChangedEventArgs changedEventArgs)
        {
            var initialArgs = changedEventArgs.ShallowCopy();
            //start progress notification thread
            new Thread(_ =>
            {
                log.Debug($"Progress thread started. File {changedEventArgs.FileUrl}");
                bool completed = false;
                //send zero progress
                OnProgressChanged(initialArgs);

                lock (changedEventArgs)
                {
                    while (!stopProgressThread && !completed)
                    {
                        Monitor.Wait(changedEventArgs);
                        var sentArgs = changedEventArgs.ShallowCopy();
                        Monitor.PulseAll(changedEventArgs);
                        completed = sentArgs.Progress == sentArgs.FileLength;
                        OnProgressChanged(sentArgs);
                    }
                }
                log.Debug($"Progress thread stopped. File {changedEventArgs.FileUrl}");
            }) { IsBackground = true }.Start();
        }

        private void StartBandwidthThread(ProgressChangedEventArgs changedEventArgs)
        {
            new Thread(_ =>
                {
                    log.Debug($"Bandwidth thread started. File {changedEventArgs.FileUrl}");
                    var bandwidth = new Bandwidth { FileUrl = changedEventArgs.FileUrl, Measures = new List<BandwidthMeasure>()};
                    var sw = Stopwatch.StartNew();
                    bool completed = false;
                    while (!stopProgressThread && !completed)
                    {
                        ProgressChangedEventArgs bandwidthArgs;
                        lock (changedEventArgs)
                        {
                            bandwidthArgs = changedEventArgs.ShallowCopy();
                        }

                        completed = bandwidthArgs.Progress == bandwidthArgs.FileLength;
                        CalculateBandwidth(bandwidth, changedEventArgs.Progress, changedEventArgs.FileLength, sw);
                        OnBandwidthMeasure(bandwidth);
                        Thread.Sleep(100);
                    }
                    sw.Stop();
                    log.Debug($"Bandwidth thread stopped. File {changedEventArgs.FileUrl}");
                })
                { IsBackground = true }.Start();
        }

        private Bandwidth CalculateBandwidth(Bandwidth bandwidth, long progress, long fileLength, Stopwatch sw)
        {
            var measure = new BandwidthMeasure
                {ElapsedMs = sw.ElapsedMilliseconds, ProgressBytes = progress, TotalBytes = fileLength};

            //we suppose that every measure is created in 100ms interval
            bandwidth.Measures.Add(measure);

            bandwidth.Mean1Second = CalculateBandwidthForPeriod(bandwidth.Measures, 1000, bandwidth.Mean1Second);
            bandwidth.Mean5Seconds = CalculateBandwidthForPeriod(bandwidth.Measures, 5000, bandwidth.Mean5Seconds);
            bandwidth.Mean30Seconds = CalculateBandwidthForPeriod(bandwidth.Measures, 30000, bandwidth.Mean30Seconds);
            bandwidth.Mean1Minute = CalculateBandwidthForPeriod(bandwidth.Measures, 60000, bandwidth.Mean1Minute);

            bandwidth.Remaining = bandwidth.Mean1Minute > 0 ? (fileLength - progress)/bandwidth.Mean1Minute : (long?)null;

            return bandwidth;
        }

        private long CalculateBandwidthForPeriod(List<BandwidthMeasure> measures, long periodMs, long defaultBandwidth)
        {
            var i = measures.Count - 1;
            var measure = measures[i];
            long time = 0;
            //calculate 1s
            while (i >= 0 && (time = measure.ElapsedMs - measures[i].ElapsedMs) < periodMs) i--;
            time = i >= 0 ? time : measure.ElapsedMs;
            long bytes = measure.ProgressBytes - (i >= 0 ? measures[i].ProgressBytes : 0);

            return time > 0 ? 1000 * bytes / time : defaultBandwidth;
        }

        public void OnBandwidthMeasure(Bandwidth bandwidth)
        {
            BandwidthMeasured?.Invoke(this, new BandwidthEventArgs { Bandwidth = bandwidth });
        }

        public void OnDownloadComplete(DownloadResult result)
        {
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs { Result = result});
        }

        public void OnProgressChanged(ProgressChangedEventArgs eventArgs)
        {
            ProgressChanged?.Invoke(this, eventArgs);
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
                        info.IsSupportedRange = webResponse.StatusCode == HttpStatusCode.PartialContent
                                                || webResponse.GetResponseHeader("Accept-Ranges") == "bytes";

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

        public DownloadBatch CreateBatch() => new DownloadBatch() { BatchId = Interlocked.Increment(ref batchId)};

        public void DownloadAsync(DownloadBatch batch, string fileUrl, string filePath, int numberOfParallelDownloads = 0, bool validateSSL = false)
        {
            throw new NotImplementedException();
        }

        public void DownloadAsync(string fileUrl, int numberOfParallelDownloads = 0,
            bool validateSSL = false) =>
            DownloadAsync(fileUrl, new Uri(fileUrl).Segments.Last());

        public void DownloadAsync(string fileUrl, string filePath, int numberOfParallelDownloads = 0,
            bool validateSSL = false)
        {
            new Thread(_ => Download(fileUrl, filePath, numberOfParallelDownloads, validateSSL ))
                { IsBackground = true}.Start();
        }

        public DownloadResult Download(string fileUrl, int numberOfParallelDownloads = 0, bool validateSSL = false)
            => Download(fileUrl, new Uri(fileUrl).Segments.Last(), numberOfParallelDownloads, validateSSL);

        public DownloadResult Download(string fileUrl, string filePath, int numberOfParallelDownloads = 0, bool validateSSL = false)
        {
            if (!validateSSL)
            {
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            }

            Uri uri = new Uri(fileUrl);

            var downloadFileInfo = GetFileInfo(fileUrl);

            log.Info($"HEAD supported: {downloadFileInfo.IsSupportedHead}, Range supported: {downloadFileInfo.IsSupportedRange}");
            log.Info($"File {fileUrl} exists: {downloadFileInfo.Exists}, length = {downloadFileInfo.Length}");
            log.Info($"{downloadFileInfo.Length}");

            if (!downloadFileInfo.Exists)
                return new DownloadResult { FileExists = false };

            var result = new DownloadResult { FileUrl = fileUrl, FilePath = filePath, FileExists = true };

            //Handle number of parallel downloads  
            if (numberOfParallelDownloads <= 0)
            {
                numberOfParallelDownloads = Environment.ProcessorCount;
            }

            var readRanges = PrepareRanges(downloadFileInfo, numberOfParallelDownloads);

            var sw = Stopwatch.StartNew();
            long bytesDownloaded = DownloadRanges(downloadFileInfo, readRanges);
            sw.Stop();

            result.TimeTakenMs = sw.ElapsedMilliseconds;
            result.ParallelDownloads = readRanges.Count;
            result.BytesDownloaded = bytesDownloaded;

            WriteFile(filePath, readRanges);

            OnDownloadComplete(result);

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
            }

            return readRanges;
        }

        private long DownloadRanges(DownloadFileInfo info, List<Range> readRanges)
        { 
            // Parallel download  
            long bytesDownloaded = 0;
            int numberOfThreads = readRanges.Count;
            var mutex = new ManualResetEvent(false);
            var progressChangedEventArgs = new ProgressChangedEventArgs { FileUrl = info.FileUrl, FileLength = info.Length};
            StartProgressThread(progressChangedEventArgs);
            StartBandwidthThread(progressChangedEventArgs);

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
                            var httpWebRequest = (HttpWebRequest)WebRequest.Create(info.FileUrl);
                            httpWebRequest.Method = "GET";
                            if (info.IsSupportedRange)
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

                                        lock (progressChangedEventArgs)
                                        {
                                            progressChangedEventArgs.Progress = bytesDownloaded;
                                            Monitor.PulseAll(progressChangedEventArgs);
                                        }
                                    }
                                }

                                success = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            //reset offset if server does not support range
                            if (!info.IsSupportedRange)
                                offset = 0;

                            log.Warn($"Exception {ex.Message} index={readRange.Index} offset={offset}");
                            WaitNetwork(ref triesCount);
                        }
                    }

                    if (Interlocked.Decrement(ref numberOfThreads) == 0)
                        mutex.Set();
                    log.Debug($"{readRange.Index} completed. Range: {readRange.Start}-{readRange.End}");

                }){ IsBackground = true}.Start();
            }

            mutex.WaitOne();

            return bytesDownloaded;
        }

        private void WaitNetwork(ref int triesCount)
        {
            triesCount++;
            Thread.Sleep(NetErrorWaitMs);
        }

        private void ReleaseUnmanagedResources()
        {
            // TODO release unmanaged resources here
        }

        protected virtual void Dispose(bool disposing)
        {
            ReleaseUnmanagedResources();
            if (disposing)
            {
                stopProgressThread = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~GidoraDownloader()
        {
            Dispose(false);
        }
    }
}