using System;
using System.Threading;

namespace TimberNet
{
    /**
     * Writes to one stream from its own thread, in the order data was queued,
     * so the game's thread never waits on the network or the rate limit.
     */
    public class StreamSender
    {
        private readonly ISocketStream stream;
        private readonly Action<string> log;
        // null asks the thread to stop
        private readonly ConcurrentQueueWithWait<byte[]?> queue = new ConcurrentQueueWithWait<byte[]?>();
        private volatile bool stopped = false;

        public StreamSender(ISocketStream stream, Action<string> log)
        {
            this.stream = stream;
            this.log = log;
            Thread thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"TimberNet sender ({stream.Name ?? "TCP"})",
            };
            thread.Start();
        }

        /**
         * Queues data to be sent with its length in front.
         */
        public void Send(byte[] data)
        {
            if (stopped) return;
            queue.Enqueue(data);
        }

        /**
         * Ends the thread once what's already queued has gone out.
         */
        public void Stop()
        {
            if (stopped) return;
            stopped = true;
            queue.Enqueue(null);
        }

        private void Run()
        {
            while (true)
            {
                if (!queue.WaitAndTryDequeue(out byte[]? data)) continue;
                if (data == null) return;
                if (!stream.Connected)
                {
                    // Gone: nothing more can reach them
                    stopped = true;
                    return;
                }
                try
                {
                    WriteWithLength(stream, data);
                }
                catch (Exception e)
                {
                    log($"Error sending data: {e.Message}");
                }
            }
        }

        public static void WriteLength(ISocketStream stream, int length)
        {
            byte[] buffer = BitConverter.GetBytes(length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(buffer);
            stream.Write(buffer, 0, buffer.Length);
        }

        /**
         * Writes the length and then the data in chunks, pausing between
         * them to keep under the stream's rate limit. Blocks until done.
         */
        public static void WriteWithLength(ISocketStream stream, byte[] data)
        {
            WriteLength(stream, data.Length);
            int chunkSize = stream.MaxChunkSize;
            // How long to sleep between chunks (may be 0)
            int sleepMS = stream.MaxChunkSize * 1000 / stream.MaxBytesPerSecond;
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                if (i != 0)
                {
                    Thread.Sleep(sleepMS);
                }
                // Gone halfway through: the rest of a map would only take time
                if (!stream.Connected) return;
                int length = Math.Min(chunkSize, data.Length - i);
                stream.Write(data, i, length);
            }
        }
    }
}
