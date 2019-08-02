using System;

namespace Downloader.App
{
    public class DownloadResult
    {
        public bool FileExists { get; set; }
        public long Size { get; set; }
        public string FilePath { get; set; }
        public long TimeTakenMs { get; set; }
        public int ParallelDownloads { get; set; }
    }
}