#if NET_3_5

namespace Downloader.Gidora
{
    public class CancellationTokenSource
    {
        public CancellationToken Token { get; private set; }

        public CancellationTokenSource()
        {
            Token = new CancellationToken();
        }

        public void Cancel()
        {
            Token.Cancel();
        }
    }
}

#endif
