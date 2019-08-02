using System.Threading;

namespace Downloader.Gidora
{
    internal class Range
    {
        public long Start { get; set; }
        public long End { get; set; }
        public byte[] Buffer { get; set; }
        public ManualResetEvent Mutex { get; set; }
        public int Index { get; set; }
    }
}