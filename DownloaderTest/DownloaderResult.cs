using System;

namespace Downloader.App
{
    public class DownloadResult
    {
        public long Size { get; set; }
        public String FilePath { get; set; }
        public long TimeTakenMs { get; set; }
        public int ParallelDownloads { get; set; }
    }
}