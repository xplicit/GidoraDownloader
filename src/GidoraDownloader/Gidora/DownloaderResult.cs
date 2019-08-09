namespace Downloader.Gidora
{
    public class DownloadResult
    {
        public string FileUrl { get; set; }
        public string FilePath { get; set; }
        public bool FileExists { get; set; }
        public long BytesDownloaded { get; set; }
        public long TimeTakenMs { get; set; }
        public int ParallelDownloads { get; set; }
    }
}